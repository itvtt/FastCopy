using System.IO;
using System.Windows;

namespace FastCopy.App;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        MoveFormerDataFolder();
        // 사용 시간 · 복사 용량 집계 (AppKit\AppUsage.cs → API 서버, ImPerformance 성과 계산용)
        ILuncher.AppKit.AppUsage.Start();
        // i-luncher AppKit: 설치된 사본이면 시작할 때 GitHub 릴리스를 확인해 스스로 업데이트 (AppKit\AppUpdater.cs)
        ILuncher.AppKit.AppUpdater.StartBackground(ready => Dispatcher.BeginInvoke(() =>
        {
            if (MessageBox.Show($"새 버전 {ready.Update.Version}을(를) 받았습니다. (지금 {ready.CurrentVersion})\n\n지금 다시 시작할까요? [아니요]를 누르면 종료할 때 적용됩니다.",
                    "FastCopy 업데이트", MessageBoxButton.YesNo, MessageBoxImage.Information) == MessageBoxResult.Yes)
            {
                ready.RestartNow();
                Shutdown();
            }
        }));
    }

    /// <summary>예전 이름(SwiftCopy, 2026-11-16 FastCopy 로 바꿈)의 기록 폴더 %LOCALAPPDATA%\SwiftCopy 를 FastCopy 로 옮김 (새 폴더가 아직 없을 때 한 번)</summary>
    private static void MoveFormerDataFolder()
    {
        string local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        string former = Path.Combine(local, "SwiftCopy"), current = Path.Combine(local, "FastCopy");
        try
        {
            if (Directory.Exists(former) && !Directory.Exists(current)) Directory.Move(former, current);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
    }
}
