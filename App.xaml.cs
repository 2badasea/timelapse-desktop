using System.Windows;
using System.Windows.Threading;
using TimelapseConverter.Helpers;

namespace TimelapseConverter;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        DevSnapshot.Parse(e.Args);
        // 이전 실행에서 남은 미리보기 파일 정리
        AppPaths.CleanTemp();
        DispatcherUnhandledException += OnUnhandledException;
    }

    protected override void OnExit(ExitEventArgs e)
    {
        AppPaths.CleanTemp();
        base.OnExit(e);
    }

    void OnUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        MessageBox.Show("예상하지 못한 오류가 발생했습니다.\n\n" + e.Exception.Message,
            "TimeLapse Converter", MessageBoxButton.OK, MessageBoxImage.Error);
        e.Handled = true;
    }
}
