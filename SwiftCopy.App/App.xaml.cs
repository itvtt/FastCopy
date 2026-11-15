using System.Windows;

namespace SwiftCopy.App;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
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
}
