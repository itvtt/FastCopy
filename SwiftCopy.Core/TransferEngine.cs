using System.ComponentModel;
using System.Security.Cryptography;
using System.Threading.Channels;

namespace SwiftCopy.Core;

public static class TransferEngine
{
    private sealed record WorkItem(string Source, string Destination, long Length);
    private sealed record DirectoryItem(string Source, string Destination);

    public static string Normalize(string path) => Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
    private static bool Within(string path, string parent) => path.Equals(parent, StringComparison.OrdinalIgnoreCase)
        || path.StartsWith(Path.EndsInDirectorySeparator(parent) ? parent : parent + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);

    private static bool HasSelectedAncestor(string path, HashSet<string> selectedPaths)
    {
        string? current = path;
        while (current != null)
        {
            if (selectedPaths.Contains(current)) return true;
            string? parent = Path.GetDirectoryName(current);
            if (parent == current) break;
            current = parent;
        }
        return false;
    }

    public static IReadOnlyList<string> Validate(TransferOptions options)
    {
        if (options.Workers is < 1 or > 32) throw new ArgumentException("병렬 작업 수는 1~32여야 합니다.");
        if (options.Sources.Count == 0) throw new ArgumentException("복사할 파일 또는 폴더를 추가하세요.");
        if (string.IsNullOrWhiteSpace(options.Destination)) throw new ArgumentException("대상 폴더를 선택하세요.");
        string destination = Normalize(options.Destination);
        EnsureNoLinks(destination);
        if (File.Exists(destination)) throw new ArgumentException("대상은 폴더여야 합니다.");
        var roots = options.Sources.Select(Normalize).Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(x => x.Length).ToList();
        var selected = new List<string>();
        var selectedDirectories = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var targets = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (string source in roots)
        {
            bool isDirectory = Directory.Exists(source);
            if (!isDirectory && !File.Exists(source)) throw new FileNotFoundException("원본을 찾을 수 없습니다.", source);
            EnsureNoLinks(source);
            string? parent = Path.GetDirectoryName(source);
            if (parent != null && HasSelectedAncestor(parent, selectedDirectories)) continue;
            string name = Path.GetFileName(source);
            if (string.IsNullOrEmpty(name)) throw new ArgumentException("드라이브 전체 대신 드라이브 안의 폴더를 선택하세요.");
            string target = Path.Combine(destination, name);
            if (Within(destination, source) || target.Equals(source, StringComparison.OrdinalIgnoreCase))
                throw new ArgumentException("대상은 원본 자신 또는 원본 폴더 내부일 수 없습니다.");
            if (!targets.Add(target)) throw new ArgumentException($"이름이 같은 원본이 있습니다: {name}. 각각 다른 대상에 전송하세요.");
            selected.Add(source);
            if (isDirectory) selectedDirectories.Add(source);
        }
        var selectedPaths = new HashSet<string>(selected, StringComparer.OrdinalIgnoreCase);
        foreach (string source in selected)
            if (HasSelectedAncestor(Path.Combine(destination, Path.GetFileName(source)), selectedPaths))
                throw new ArgumentException("대상 경로가 다른 원본과 겹칩니다.");
        return selected;
    }

    private static void EnsureNoLinks(string path)
    {
        for (string? current = path; current != null; current = Path.GetDirectoryName(current))
            if (IsLink(NativeFiles.Attributes(current))) throw LinkError(current);
    }

    private static bool IsLink(uint attributes) =>
        attributes != NativeFiles.MissingAttributes && (attributes & (uint)FileAttributes.ReparsePoint) != 0;

    private static IOException LinkError(string path) => new($"안전한 전송을 위해 링크/정션 경로는 지원하지 않습니다: {path}");

    /// <summary>
    /// Links and junctions on the way to a file, checked per run: every folder (and its parents) once, then only the file itself.
    /// Checking every parent again for each file was a large share of the time for many small files (2026-11-12, 20,000 files).
    /// </summary>
    private sealed class LinkGuard
    {
        private readonly System.Collections.Concurrent.ConcurrentDictionary<string, byte> _checked = new(StringComparer.OrdinalIgnoreCase);

        public void Folder(string? path)
        {
            for (string? current = path; current != null && !_checked.ContainsKey(current); current = Path.GetDirectoryName(current))
            {
                if (IsLink(NativeFiles.Attributes(current))) throw LinkError(current);
                _checked.TryAdd(current, 0);
            }
        }

        /// <summary>The file's attributes (missing when it does not exist), after its folders and the file itself are checked.</summary>
        public uint File(string path)
        {
            Folder(Path.GetDirectoryName(path));
            uint attributes = NativeFiles.Attributes(path);
            if (IsLink(attributes)) throw LinkError(path);
            return attributes;
        }
    }

    // New files below this size are copied straight to their name: the temporary file and its rename cost more than the copy itself
    // for small files (20,000 files of 4 KB: 8 s straight, 14.6 s through a temporary file). A failed or cancelled copy removes what
    // it wrote (the name did not exist before: the copy refuses an existing name). Larger files and overwrites keep the temporary
    // file, so a crash never leaves a long half-written file, and an overwrite keeps the old file until the new one is complete.
    internal const long DirectCopyLimit = 8L * 1024 * 1024;

    // Batch limits (see the queue in RunAsync): up to this many files, or bytes, from one folder go to one worker.
    private const int BatchFiles = 256;
    private const long BatchBytes = 32L * 1024 * 1024;

    public static async Task<TransferResult> RunAsync(TransferOptions options, TransferSession session, CancellationToken token = default)
    {
        var roots = Validate(options);
        string destination = Normalize(options.Destination);
        Directory.CreateDirectory(destination);
        // 폴더를 새 위치로 그냥 복사할 때는 robocopy (작은 파일이 많을 때 더 빠름, RobocopyTransfer 설명)
        if (RobocopyTransfer.CanUse(options, roots, destination)) return await RobocopyTransfer.RunAsync(options, roots, destination, session, token);
        session.InitializeWorkers(options.Workers);
        // Work goes out in small batches of files from one folder: one worker creating them one after another is faster than
        // several workers creating files in the same folder at once (20,000 small files: about 8% faster, closer to robocopy).
        var queue = Channel.CreateBounded<WorkItem[]>(new BoundedChannelOptions(1024) { SingleWriter = true, FullMode = BoundedChannelFullMode.Wait });
        var directories = new List<DirectoryItem>();
        session.Log("START", destination, $"Workers={options.Workers}; Move={options.Move}; Verify={options.Verify || options.Move}; Conflict={options.Conflict}");
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(token);
        var runToken = linked.Token;
        var guard = new LinkGuard();
        var producer = Task.Run(async () =>
        {
            var batch = new List<WorkItem>(BatchFiles);
            long batchBytes = 0;
            try
            {
                foreach (string root in roots)
                {
                    session.Wait(runToken);
                    string target = Path.Combine(destination, Path.GetFileName(root));
                    if (Directory.Exists(root))
                    {
                        // A whole directory can be renamed without reading its hundreds of thousands
                        // of entries. Existing target folders use the normal per-file merge pipeline.
                        bool renamed = false;
                        if (options.Move && !Directory.Exists(target) && !File.Exists(target))
                        {
                            try
                            {
                                EnsureNoLinks(root); EnsureNoLinks(target);
                                renamed = NativeFiles.TryRename(root, target, false);
                                if (renamed)
                                {
                                    Interlocked.Increment(ref session.MovedDirectories);
                                    session.Log("RENAME_DIRECTORY", root, target);
                                }
                            }
                            catch (Win32Exception ex) when (ex.NativeErrorCode is 80 or 183) { }
                            catch (Win32Exception ex) { session.Error(root, ex); continue; }
                        }
                        if (!renamed) await Walk(root, target);
                    }
                    else
                    {
                        await Enqueue(new FileInfo(root), target);
                        await Flush();
                    }
                }
                session.ScanComplete = true;
            }
            finally { queue.Writer.TryComplete(); }

            async Task Enqueue(FileInfo file, string target)
            {
                session.Wait(runToken);
                long length = file.Length;
                Interlocked.Increment(ref session.FoundFiles);
                Interlocked.Add(ref session.FoundBytes, length);
                // A large file is a batch of its own, so several large files still copy side by side.
                if (length >= BatchBytes) { await queue.Writer.WriteAsync([new WorkItem(file.FullName, target, length)], runToken); return; }
                batch.Add(new WorkItem(file.FullName, target, length));
                batchBytes += length;
                if (batch.Count >= BatchFiles || batchBytes >= BatchBytes) await Flush();
            }
            async Task Flush()
            {
                if (batch.Count == 0) return;
                WorkItem[] items = [.. batch];
                batch.Clear();
                batchBytes = 0;
                await queue.Writer.WriteAsync(items, runToken);
            }
            async Task Walk(string root, string target)
            {
                var pending = new Stack<DirectoryItem>();
                pending.Push(new(root, target));
                while (pending.TryPop(out var dir))
                {
                    session.Wait(runToken);
                    try
                    {
                        guard.Folder(dir.Source);
                        guard.Folder(dir.Destination);
                        Directory.CreateDirectory(dir.Destination);
                        directories.Add(dir);
                        foreach (var entry in new DirectoryInfo(dir.Source).EnumerateFileSystemInfos())
                        {
                            session.Wait(runToken);
                            if ((entry.Attributes & FileAttributes.ReparsePoint) != 0)
                            {
                                Interlocked.Increment(ref session.Skipped);
                                session.Log("SKIP_LINK", entry.FullName);
                                continue;
                            }
                            string output = Path.Combine(dir.Destination, entry.Name);
                            if (entry is DirectoryInfo) pending.Push(new(entry.FullName, output));
                            else
                            {
                                try { await Enqueue((FileInfo)entry, output); }
                                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { session.Error(entry.FullName, ex); }
                            }
                        }
                        await Flush();  // a batch never mixes folders
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { session.Error(dir.Source, ex); }
                }
            }
        }, runToken);

        var workers = Enumerable.Range(0, options.Workers).Select(id => Task.Factory.StartNew(() =>
        {
            try
            {
                while (queue.Reader.WaitToReadAsync(runToken).AsTask().GetAwaiter().GetResult())
                {
                    while (queue.Reader.TryRead(out var items))
                    foreach (var item in items)
                    {
                        session.BeginWorker(id, item.Source, item.Destination, item.Length);
                        session.Wait(runToken);
                        try
                        {
                            var state = Transfer(item, options, session, guard, id, runToken);
                            session.FinishWorkerFile(id, state);
                        }
                        catch (OperationCanceledException)
                        {
                            session.SetWorkerState(id, TransferWorkerState.Cancelled);
                            throw;
                        }
                        catch (Exception ex)
                        {
                            session.Error(item.Source, ex);
                            session.FinishWorkerFile(id, TransferWorkerState.Failed);
                        }
                        finally { Interlocked.Add(ref session.ResolvedBytes, item.Length); }
                    }
                }
            }
            catch (OperationCanceledException)
            {
                linked.Cancel();
                throw;
            }
            catch
            {
                session.SetWorkerState(id, TransferWorkerState.Failed);
                linked.Cancel();
                throw;
            }
        }, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default)).ToArray();

        bool cancelled = false;
        try { await Task.WhenAll(workers.Append(producer)); }
        catch (OperationCanceledException) when (runToken.IsCancellationRequested) { cancelled = true; }
        finally { session.Watch.Stop(); }

        if (options.Move && !cancelled)
        {
            foreach (var dir in directories.AsEnumerable().Reverse())
            {
                if (token.IsCancellationRequested) { cancelled = true; break; }
                try
                {
                    EnsureNoLinks(dir.Source);
                    // Never recurse: skipped, failed, or newly-created files keep their parent intact.
                    if (!Directory.EnumerateFileSystemEntries(dir.Source).Any()) Directory.Delete(dir.Source, false);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { session.Error(dir.Source, ex); }
            }
        }
        if (cancelled) session.CancelUnfinishedWorkers();
        var snapshot = session.Snapshot();
        session.Log(cancelled ? "CANCELLED" : "FINISHED", destination,
            $"Copied={snapshot.CopiedFiles}; Bytes={snapshot.CopiedBytes}; RenamedDirectories={snapshot.MovedDirectories}; Skipped={snapshot.Skipped}; Errors={snapshot.Errors}");
        session.FinishLog();
        return new(snapshot, cancelled);
    }

    private static TransferWorkerState Transfer(WorkItem item, TransferOptions options, TransferSession session, LinkGuard guard,
        int workerId, CancellationToken token)
    {
        guard.File(item.Source);
        // Straight to the name (small, new) or through a temporary file that is renamed once complete (see DirectCopyLimit).
        bool direct = options.Conflict != ConflictPolicy.Overwrite && item.Length < DirectCopyLimit;
        string folder = Path.GetDirectoryName(item.Destination)!;
        if (direct && !options.Move)
        {
            // The straight copy refuses an existing name (or a link at that name) by itself (80/183 below): no extra check per file.
            guard.Folder(folder);
        }
        else
        {
            uint existing = guard.File(item.Destination);
            bool exists = existing != NativeFiles.MissingAttributes && (existing & (uint)FileAttributes.Directory) == 0;
            if (exists && options.Conflict == ConflictPolicy.Skip)
            {
                Interlocked.Increment(ref session.Skipped);
                session.Log("SKIP_EXISTS", item.Source);
                return TransferWorkerState.Skipped;
            }
        }
        if (options.Move)
        {
            session.SetWorkerState(workerId, TransferWorkerState.Moving);
            var moveState = TryFastMove(item, options, session, workerId, token);
            if (moveState.HasValue) return moveState.Value;
        }
        string committed = item.Destination;
        int suffix = 1;
        string temporary = direct ? committed : Path.Combine(folder, $".swiftcopy-{Guid.NewGuid():N}.partial");
        bool kept = false;
        string? created = null;
        long transferred = 0;
        try
        {
            // The source is held open (writers and deletion denied) only when it is verified or moved: a plain copy does not
            // open every file a second time (20,000 small files: that open was a large share of the time next to robocopy).
            bool holdSource = options.Move || options.Verify;
            using var source = holdSource ? NativeFiles.OpenSource(item.Source, options.Move) : null;
            long length = source?.Length ?? item.Length;
            session.SetWorkerTotal(workerId, length);
            session.SetWorkerState(workerId, TransferWorkerState.Copying);
            // Small files need no progress callback (it cost more than the copy); pause and cancel act between them.
            Action<long>? progress = length < DirectCopyLimit ? null : value =>
            {
                Interlocked.Add(ref session.InFlightBytes, value - transferred);
                transferred = value;
                session.SetWorkerProgress(workerId, value);
            };
            for (int attempt = 0; ; )
            {
                try
                {
                    // The copy refuses an existing name, so what is at this name after it starts is its own.
                    created = temporary;
                    NativeFiles.Copy(item.Source, temporary, options.UnbufferedLargeFiles && length >= 256L * 1024 * 1024,
                        session, progress, token);
                    break;
                }
                // The name was taken meanwhile (the copy refuses an existing name): skip it, or the next free one for 둘 다 유지.
                catch (Win32Exception ex) when (direct && ex.NativeErrorCode is 80 or 183)
                {
                    created = null;  // someone else's file: never removed
                    if (options.Conflict == ConflictPolicy.Skip)
                    {
                        Interlocked.Increment(ref session.Skipped);
                        session.Log("SKIP_EXISTS", item.Source);
                        return TransferWorkerState.Skipped;
                    }
                    committed = temporary = Path.Combine(folder, $"{Path.GetFileNameWithoutExtension(item.Destination)} ({suffix++}){Path.GetExtension(item.Destination)}");
                    session.SetWorkerDestination(workerId, committed);
                }
                catch (Win32Exception ex) when (attempt < 2 && ex.NativeErrorCode is 32 or 33 or 64 or 121)
                {
                    attempt++;
                    RemoveTemporary(temporary);
                    Interlocked.Add(ref session.InFlightBytes, -transferred); transferred = 0;
                    session.SetWorkerProgress(workerId, 0);
                    session.Log("RETRY", item.Source, ex.Message);
                    if (token.WaitHandle.WaitOne(300 * attempt)) token.ThrowIfCancellationRequested();
                }
            }
            if ((holdSource || !direct) && new FileInfo(temporary).Length != length) throw new IOException("복사한 파일의 크기가 일치하지 않습니다.");
            if (source is not null)
            {
                session.SetWorkerState(workerId, TransferWorkerState.Verifying);
                source.Position = 0;
                byte[] original = Hash(source, session, token);
                using var target = new FileStream(temporary, FileMode.Open, FileAccess.Read, FileShare.Read, 1024 * 1024, FileOptions.SequentialScan);
                if (!original.AsSpan().SequenceEqual(Hash(target, session, token))) throw new IOException("SHA-256 검증에 실패했습니다. 원본을 보존합니다.");
            }
            session.Wait(token);
            if (!direct) guard.File(committed);  // a straight copy created this name itself
            if (direct)
            {
                // Already at its name: complete (and checked) from here on.
                kept = true;
            }
            else if (options.Conflict == ConflictPolicy.KeepBoth)
            {
                while (true)
                {
                    token.ThrowIfCancellationRequested();
                    try { File.Move(temporary, committed, false); break; }
                    catch (IOException) when (File.Exists(committed) || Directory.Exists(committed))
                    {
                        committed = Path.Combine(folder, $"{Path.GetFileNameWithoutExtension(item.Destination)} ({suffix++}){Path.GetExtension(item.Destination)}");
                        session.SetWorkerDestination(workerId, committed);
                    }
                }
            }
            else
            {
                try { File.Move(temporary, committed, options.Conflict == ConflictPolicy.Overwrite); }
                catch (IOException) when (options.Conflict == ConflictPolicy.Skip && File.Exists(committed))
                {
                    Interlocked.Increment(ref session.Skipped);
                    session.Log("SKIP_EXISTS", item.Source);
                    return TransferWorkerState.Skipped;
                }
            }
            // Cancellation after commit deliberately leaves the original in place.
            token.ThrowIfCancellationRequested();
            if (options.Move) NativeFiles.DeleteSource(source!);
            Interlocked.Increment(ref session.CopiedFiles);
            Interlocked.Add(ref session.CopiedBytes, length);
            return TransferWorkerState.Completed;
        }
        finally
        {
            Interlocked.Add(ref session.InFlightBytes, -transferred);
            // Only what this copy wrote: a temporary file, or a straight copy that did not finish (its name was free before).
            if (!kept && created is not null)
            {
                try { RemoveTemporary(created); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { session.Error(created, ex); }
            }
        }
    }

    private static byte[] Hash(Stream stream, TransferSession session, CancellationToken token)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        byte[] buffer = System.Buffers.ArrayPool<byte>.Shared.Rent(1024 * 1024);
        try
        {
            int read;
            while (true)
            {
                session.Wait(token);
                read = stream.Read(buffer, 0, buffer.Length);
                if (read == 0) break;
                hash.AppendData(buffer, 0, read);
            }
            return hash.GetHashAndReset();
        }
        finally { System.Buffers.ArrayPool<byte>.Shared.Return(buffer); }
    }

    private static TransferWorkerState? TryFastMove(WorkItem item, TransferOptions options, TransferSession session,
        int workerId, CancellationToken token)
    {
        string target = item.Destination;
        int suffix = 1;
        while (true)
        {
            session.Wait(token);
            session.SetWorkerDestination(workerId, target);
            try
            {
                if (!NativeFiles.TryRename(item.Source, target, options.Conflict == ConflictPolicy.Overwrite)) return null;
                Interlocked.Increment(ref session.CopiedFiles);
                Interlocked.Add(ref session.CopiedBytes, item.Length);
                return TransferWorkerState.Completed;
            }
            catch (Win32Exception ex) when (ex.NativeErrorCode is 80 or 183)
            {
                if (options.Conflict == ConflictPolicy.Skip)
                {
                    Interlocked.Increment(ref session.Skipped); session.Log("SKIP_EXISTS", item.Source); return TransferWorkerState.Skipped;
                }
                if (options.Conflict != ConflictPolicy.KeepBoth) throw;
                target = Path.Combine(Path.GetDirectoryName(item.Destination)!, $"{Path.GetFileNameWithoutExtension(item.Destination)} ({suffix++}){Path.GetExtension(item.Destination)}");
            }
        }
    }

    private static void RemoveTemporary(string path)
    {
        if (!File.Exists(path)) return;
        File.SetAttributes(path, FileAttributes.Normal);
        File.Delete(path);
    }
}
