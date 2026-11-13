using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;

namespace SwiftCopy.Core;

/// <summary>
/// 폴더를 새 위치로 복사할 때는 Windows 의 robocopy 로 복사함 (같은 동시 작업 수).
/// 같은 Windows 복사 함수를 써도 Defender 가 robocopy 가 만든 파일을 훨씬 덜 검사해서, 작은 파일이 많을 때 더 빠름
/// (4 KB 파일 2만 개: SwiftCopy 엔진 7.3초, robocopy 6.0초. 2026-11-13 측정, README).
/// 쓰는 경우: 복사(이동 아님), 검증 안 함, 원본이 모두 폴더이고 대상에 같은 이름이 아직 없음 (새로 만드는 폴더라 덮어쓰기·건너뛰기가 없음).
/// 취소하면 robocopy 를 끝내고, 원본과 크기·수정 시각이 다른 파일(덜 복사된 것)을 지움. 대상 폴더는 이번에 새로 만든 것이라 다른 파일이 없음.
/// </summary>
internal static partial class RobocopyTransfer
{
    private static readonly string Robocopy = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "robocopy.exe");

    /// <summary>테스트·비교용: SWIFTCOPY_ENGINE=own 이면 쓰지 않음</summary>
    internal static bool CanUse(TransferOptions options, IReadOnlyList<string> roots, string destination) =>
        Environment.GetEnvironmentVariable("SWIFTCOPY_ENGINE") != "own"
        && !options.Move && !options.Verify && File.Exists(Robocopy) && roots.Count > 0
        && roots.All(root => Directory.Exists(root)
                             && !Directory.Exists(Path.Combine(destination, Path.GetFileName(root)))
                             && !File.Exists(Path.Combine(destination, Path.GetFileName(root))));

    // 로그 한 줄: "\t<상태(언어마다 다름)>\t\t<바이트>\t<원본 전체 경로>"
    [GeneratedRegex(@"\t\s*(\d+)\t(.+)$")]
    private static partial Regex FileLine();

    // 오류 줄: "2026/11/13 09:57:39 ERROR 5 (0x00000005) <동작> <경로>" (ERROR 는 언어마다 다름)
    [GeneratedRegex(@"^\d{4}/\d{2}/\d{2} \d{2}:\d{2}:\d{2} \S+ (\d+) \(0x[0-9A-Fa-f]{8}\)")]
    private static partial Regex ErrorLine();

    [DllImport("ntdll.dll")] private static extern int NtSuspendProcess(IntPtr process);
    [DllImport("ntdll.dll")] private static extern int NtResumeProcess(IntPtr process);

    /// <summary>화면 스레드를 막지 않도록 백그라운드에서 (robocopy 를 기다리는 동안 계속 로그를 읽음)</summary>
    internal static Task<TransferResult> RunAsync(TransferOptions options, IReadOnlyList<string> roots, string destination,
        TransferSession session, CancellationToken token)
    {
        session.InitializeWorkers(options.Workers);  // 작업 칸은 바로 (화면이 처음부터 같은 수를 봄)
        return Task.Factory.StartNew(() => Run(options, roots, destination, session, token), CancellationToken.None,
            TaskCreationOptions.LongRunning, TaskScheduler.Default);
    }

    private static TransferResult Run(TransferOptions options, IReadOnlyList<string> roots, string destination,
        TransferSession session, CancellationToken token)
    {
        session.Log("START", destination, $"Workers={options.Workers}; Engine=robocopy");
        // 전체 개수·크기 (진행률용): 복사와 함께 셈
        var scan = Task.Run(() =>
        {
            foreach (string root in roots)
                foreach (var file in new DirectoryInfo(root).EnumerateFiles("*", new EnumerationOptions
                         { RecurseSubdirectories = true, IgnoreInaccessible = true, AttributesToSkip = FileAttributes.ReparsePoint }))
                {
                    if (token.IsCancellationRequested) return;
                    Interlocked.Increment(ref session.FoundFiles);
                    Interlocked.Add(ref session.FoundBytes, file.Length);
                }
            session.ScanComplete = true;
        });

        bool cancelled = false;
        var started = new List<long>();  // 시작한 파일 크기 (robocopy 는 동시 복사일 때 시작할 때 기록함)
        int errors = 0, lane = 0;
        var laneUsed = new bool[options.Workers];
        foreach (string root in roots)
        {
            if (token.IsCancellationRequested) { cancelled = true; break; }
            string target = Path.Combine(destination, Path.GetFileName(root));
            string log = Path.Combine(Path.GetTempPath(), $"swiftcopy-robocopy-{Guid.NewGuid():N}.log");
            var start = new ProcessStartInfo(Robocopy) { UseShellExecute = false, CreateNoWindow = true };
            foreach (string argument in new[] { root, target, "/E", $"/MT:{options.Workers}", "/R:1", "/W:1", "/XJ", "/NP", "/NDL", "/NJH", "/NJS",
                         "/BYTES", "/FP", "/UNILOG:" + log })
                start.ArgumentList.Add(argument);
            using var process = Process.Start(start) ?? throw new IOException("robocopy 를 시작하지 못했습니다.");
            using var reader = new LogReader(log);
            bool suspended = false;
            try
            {
                while (true)
                {
                    bool exited = process.WaitForExit(150);
                    foreach (string line in reader.ReadLines(final: exited))
                    {
                        if (ErrorLine().Match(line) is { Success: true })
                        {
                            errors++;
                            Interlocked.Increment(ref session.Errors);
                            session.Log("ERROR", root, line.Trim());
                        }
                        else if (FileLine().Match(line) is { Success: true } file && long.TryParse(file.Groups[1].Value, out long size))
                        {
                            string source = file.Groups[2].Value.Trim();
                            started.Add(size);
                            int id = lane++ % options.Workers;
                            if (laneUsed[id]) session.FinishWorkerFile(id, TransferWorkerState.Completed);
                            laneUsed[id] = true;
                            session.BeginWorker(id, source, Path.Combine(target, Path.GetRelativePath(root, source)), size);
                            session.SetWorkerState(id, TransferWorkerState.Copying);
                        }
                    }
                    // 진행률: 시작한 파일 중 마지막 (동시 작업 수)개는 아직 복사 중일 수 있음
                    int done = Math.Max(0, started.Count - (exited ? 0 : options.Workers));
                    long bytes = 0;
                    for (int i = 0; i < done; i++) bytes += started[i];
                    Interlocked.Exchange(ref session.CopiedFiles, done);
                    Interlocked.Exchange(ref session.CopiedBytes, bytes);
                    Interlocked.Exchange(ref session.ResolvedBytes, bytes);
                    if (exited) break;
                    if (token.IsCancellationRequested)
                    {
                        if (suspended) NtResumeProcess(process.Handle);
                        process.Kill();
                        process.WaitForExit();
                        cancelled = true;
                        break;
                    }
                    // 일시정지: robocopy 를 멈췄다가 다시 이어 감
                    bool paused = !session.Gate.IsSet;
                    if (paused != suspended)
                    {
                        if (paused) NtSuspendProcess(process.Handle); else NtResumeProcess(process.Handle);
                        suspended = paused;
                    }
                }
            }
            finally
            {
                if (!process.HasExited) { try { process.Kill(); process.WaitForExit(); } catch (InvalidOperationException) { } }
            }
            if (!cancelled && process.ExitCode >= 8 && errors == 0)
            {
                errors++;
                Interlocked.Increment(ref session.Errors);
                session.Log("ERROR", root, $"robocopy 종료 코드 {process.ExitCode}");
            }
            if (cancelled)
            {
                RemoveUnfinished(root, target, session);
                break;
            }
        }
        try { scan.Wait(); } catch (AggregateException) { }
        session.Watch.Stop();
        for (int id = 0; id < options.Workers; id++)
            if (laneUsed[id]) session.FinishWorkerFile(id, cancelled ? TransferWorkerState.Cancelled : TransferWorkerState.Completed);
        if (!cancelled)
        {
            // 끝나면 모두 완료 (오류 난 파일은 빼고: 크기는 알 수 없으므로 개수만)
            Interlocked.Exchange(ref session.CopiedFiles, Math.Max(0, started.Count - errors));
            long total = started.Sum();
            Interlocked.Exchange(ref session.CopiedBytes, total);
            Interlocked.Exchange(ref session.ResolvedBytes, total);
        }
        else session.CancelUnfinishedWorkers();
        var snapshot = session.Snapshot();
        session.Log(cancelled ? "CANCELLED" : "FINISHED", destination,
            $"Copied={snapshot.CopiedFiles}; Bytes={snapshot.CopiedBytes}; Errors={snapshot.Errors}; Engine=robocopy");
        session.FinishLog();
        return new(snapshot, cancelled);
    }

    /// <summary>취소한 뒤: 원본과 크기·수정 시각이 다른 파일은 덜 복사된 것 (robocopy 는 미리 전체 크기를 잡고, 다 쓴 뒤 시각을 맞춤)</summary>
    private static void RemoveUnfinished(string root, string target, TransferSession session)
    {
        if (!Directory.Exists(target)) return;
        foreach (var copy in new DirectoryInfo(target).EnumerateFiles("*", new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true }))
        {
            try
            {
                var original = new FileInfo(Path.Combine(root, Path.GetRelativePath(target, copy.FullName)));
                if (original.Exists && original.Length == copy.Length && original.LastWriteTimeUtc == copy.LastWriteTimeUtc) continue;
                copy.Attributes = FileAttributes.Normal;
                copy.Delete();
                session.Log("REMOVE_PARTIAL", copy.FullName);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { session.Error(copy.FullName, ex); }
        }
    }

    /// <summary>robocopy 의 유니코드 로그(UTF-16)를 쓰는 중에 읽음. 줄이 끝난 것만 돌려줌</summary>
    private sealed class LogReader(string path) : IDisposable
    {
        private FileStream? _stream;
        private readonly Decoder _decoder = new UnicodeEncoding(false, false).GetDecoder();
        private readonly StringBuilder _pending = new();
        private readonly byte[] _bytes = new byte[64 * 1024];
        private readonly char[] _chars = new char[64 * 1024];

        public IEnumerable<string> ReadLines(bool final)
        {
            if (_stream is null)
            {
                if (!File.Exists(path)) yield break;
                try { _stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete); }
                catch (IOException) { yield break; }
            }
            int read;
            while ((read = _stream.Read(_bytes, 0, _bytes.Length)) > 0)
            {
                int count = _decoder.GetChars(_bytes, 0, read, _chars, 0);
                _pending.Append(_chars, 0, count);
            }
            string text = _pending.ToString().TrimStart('﻿');
            int end = final ? text.Length : text.LastIndexOf('\n') + 1;
            _pending.Clear().Append(text, end, text.Length - end);
            foreach (string line in text[..end].Split('\n'))
                if (line.TrimEnd('\r').Length > 0) yield return line.TrimEnd('\r');
        }

        public void Dispose()
        {
            _stream?.Dispose();
            try { File.Delete(path); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
    }
}
