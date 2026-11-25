using System.ComponentModel;
using System.Diagnostics;
using System.Windows;
using System.Windows.Media;
using TimelapseConverter.Helpers;
using TimelapseConverter.Services;

namespace TimelapseConverter;

/// <summary>
/// 새 버전 안내 + 원클릭 업데이트 창. 내려받기·검증·설치까지 하고, 끝나면 ReadyToRestart를 켜고 닫힌다.
/// (새 버전 실행과 프로그램 종료는 메인 창이 맡는다)
/// </summary>
public partial class UpdateWindow : Window
{
    readonly UpdateInfo _info;
    CancellationTokenSource? _cts;
    bool _working;       // 내려받기 또는 설치 중
    bool _installing;    // 파일 교체 중 (이때는 취소할 수 없음)
    bool _closeAfterCancel;

    public bool ReadyToRestart { get; private set; }

    public UpdateWindow(UpdateInfo info)
    {
        InitializeComponent();
        _info = info;

        string current = Updater.CurrentVersion.ToString(3);
        string next = info.Version.ToString(3);
        VersionText.Text = $"지금 v{current}  →  새 버전 v{next}" + (info.CanAutoUpdate && info.Size > 0 ? $"  ·  {Format.Size(info.Size)}" : "");
        NotesText.Text = string.IsNullOrWhiteSpace(info.Notes) ? "변경 내용 설명이 없습니다." : Format.KeepWords(info.Notes);

        if (!info.CanAutoUpdate)
        {
            InstallText.Text = "릴리스 페이지에서 받기";
            HintText.Text = "이 릴리스에는 자동 업데이트용 파일이 없어 직접 내려받아야 합니다.";
            PageButton.Visibility = Visibility.Collapsed;
        }
        Closing += OnClosing;
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        TitleBar.Apply(this);
    }

    async void Install_Click(object sender, RoutedEventArgs e)
    {
        if (!_info.CanAutoUpdate)
        {
            OpenPage();
            Close();
            return;
        }
        if (_working) return;

        _working = true;
        _cts = new CancellationTokenSource();
        SetWorkingUi(true);
        ShowProgress("내려받는 중…", 0, "");
        try
        {
            var progress = new Progress<DownloadProgress>(p =>
            {
                double percent = p.Total > 0 ? p.Done * 100.0 / p.Total : 0;
                ShowProgress($"내려받는 중… {percent:0}%", percent,
                    p.Total > 0 ? $"{Format.Size(p.Done)} / {Format.Size(p.Total)}" : Format.Size(p.Done));
            });
            string zip = await Updater.DownloadAsync(_info, progress, _cts.Token);

            // 여기부터는 파일을 바꾸는 중이라 취소하지 않는다
            _installing = true;
            LaterButton.IsEnabled = false;
            ShowProgress("확인 · 설치 중…", 100, "바뀐 파일만 교체합니다");
            await Task.Run(() => Updater.Install(zip));

            ReadyToRestart = true;
            ShowProgress("새 버전으로 다시 시작합니다…", 100, "");
            Close();
        }
        catch (OperationCanceledException)
        {
            Updater.DiscardStaging();
            ResetUi();
            if (_closeAfterCancel) Close();
            else ShowMessage("업데이트를 취소했습니다.", isError: false);
        }
        catch (Exception ex)
        {
            Updater.DiscardStaging();
            ResetUi();
            InstallText.Text = "다시 시도";
            ShowMessage(ex is UpdateException ? ex.Message : "업데이트하지 못했습니다: " + ex.Message, isError: true);
        }
    }

    void ResetUi()
    {
        _working = false;
        _installing = false;
        _cts?.Dispose();
        _cts = null;
        SetWorkingUi(false);
        ProgressPanel.Visibility = Visibility.Collapsed;
    }

    void SetWorkingUi(bool working)
    {
        InstallButton.Visibility = working ? Visibility.Collapsed : Visibility.Visible;
        PageButton.Visibility = working ? Visibility.Hidden : Visibility.Visible;
        LaterButton.Content = working ? "취소" : "나중에";
        LaterButton.IsEnabled = true;
        MessagePanel.Visibility = Visibility.Collapsed;
        ProgressPanel.Visibility = working ? Visibility.Visible : Visibility.Collapsed;
    }

    void ShowProgress(string text, double percent, string detail)
    {
        ProgressText.Text = text;
        Progress.Value = percent;
        ProgressDetailText.Text = detail;
    }

    void ShowMessage(string text, bool isError)
    {
        MessagePanel.Background = (Brush)FindResource(isError ? "DangerSoftBrush" : "SurfaceAltBrush");
        MessagePanel.BorderBrush = (Brush)FindResource(isError ? "DangerLineBrush" : "LineBrush");
        MessageText.Foreground = (Brush)FindResource(isError ? "DangerBrush" : "TextSecondaryBrush");
        MessageText.Text = text;
        MessagePanel.Visibility = Visibility.Visible;
    }

    void Later_Click(object sender, RoutedEventArgs e)
    {
        if (_working) _cts?.Cancel();
        else Close();
    }

    void Page_Click(object sender, RoutedEventArgs e) => OpenPage();

    void OpenPage()
    {
        try { Process.Start(new ProcessStartInfo(_info.PageUrl) { UseShellExecute = true }); }
        catch (Exception ex) { ShowMessage("브라우저를 열 수 없습니다: " + ex.Message, isError: true); }
    }

    void OnClosing(object? sender, CancelEventArgs e)
    {
        if (!_working || ReadyToRestart) return;
        e.Cancel = true;
        if (_installing) return;  // 설치 중에는 닫지 않음 (금방 끝남)
        _closeAfterCancel = true; // 내려받는 중이면 취소하고 정리된 뒤 닫는다
        _cts?.Cancel();
    }
}
