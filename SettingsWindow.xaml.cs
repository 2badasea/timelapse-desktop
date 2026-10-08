using System.Diagnostics;
using System.IO;
using System.Windows;
using Microsoft.Win32;
using TimelapseConverter.Helpers;
using TimelapseConverter.Services;

namespace TimelapseConverter;

/// <summary>
/// 설정 창. 바뀐 값은 바로 저장하고, 메인 창은 닫힌 뒤 Changed 플래그를 보고 갱신한다.
/// </summary>
public partial class SettingsWindow : Window
{
    readonly AppSettings _settings;
    readonly bool _gpuAvailable;
    readonly Action _releasePreview;

    public bool SourceChanged { get; private set; }
    public bool OutputChanged { get; private set; }
    public bool FfmpegChanged { get; private set; }

    public SettingsWindow(AppSettings settings, FfmpegTools? tools, bool gpuAvailable, Action releasePreview)
    {
        InitializeComponent();
        _settings = settings;
        _gpuAvailable = gpuAvailable;
        _releasePreview = releasePreview;

        FfmpegText.Text = tools != null ? "FFmpeg: " + tools.FfmpegPath : "FFmpeg를 찾지 못했습니다. 위치를 지정해 주세요.";
        FfmpegText.ToolTip = FfmpegText.Text;
        ResetOutputButton.Content = $"기본 폴더로 되돌리기 ({AppSettings.DefaultOutputFolder()})";
        var version = typeof(SettingsWindow).Assembly.GetName().Version;
        VersionText.Text = $"TimeLapse Converter v{version?.ToString(3)}";

        RefreshFolders();
        RefreshTemp();
        RefreshGpu();
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        TitleBar.Apply(this);
    }

    void RefreshFolders()
    {
        OutputFolderText.Text = _settings.OutputFolder;
        OutputFolderText.ToolTip = _settings.OutputFolder;
        SourceFolderText.Text = _settings.SourceFolder;
        SourceFolderText.ToolTip = _settings.SourceFolder;
        ResetOutputButton.Visibility =
            string.Equals(_settings.OutputFolder, AppSettings.DefaultOutputFolder(), StringComparison.OrdinalIgnoreCase)
                ? Visibility.Collapsed : Visibility.Visible;
    }

    void RefreshTemp()
    {
        var (count, bytes) = AppPaths.TempUsage();
        TempUsageText.Text = count == 0 ? "비어 있음" : $"{count}개 · {Format.Size(bytes)}";
        CleanTempButton.IsEnabled = count > 0;
        TempHintText.Text = $"미리보기를 만들 때 생기는 파일입니다. 앱을 종료할 때도 자동으로 정리됩니다.\n위치: {AppPaths.TempFolder}";
    }

    void RefreshGpu()
    {
        GpuSwitch.IsEnabled = _gpuAvailable;
        GpuSwitch.IsChecked = _gpuAvailable && _settings.UseGpu;
        GpuHintText.Text = _gpuAvailable
            ? "NVIDIA 그래픽카드로 빠르게 변환합니다. 실패하면 자동으로 CPU로 다시 시도합니다."
            : "이 PC에서는 NVIDIA 하드웨어 인코더를 쓸 수 없어 CPU로 변환합니다. (조금 느리지만 결과는 같습니다)";
    }

    // ───────── 폴더 ─────────

    void ChangeOutput_Click(object sender, RoutedEventArgs e)
    {
        string? folder = PickFolder("타임랩스 저장 폴더 선택", _settings.OutputFolder);
        if (folder == null) return;
        _settings.OutputFolder = folder;
        _settings.Save();
        OutputChanged = true;
        RefreshFolders();
    }

    void ResetOutput_Click(object sender, RoutedEventArgs e)
    {
        _settings.OutputFolder = AppSettings.DefaultOutputFolder();
        _settings.Save();
        OutputChanged = true;
        RefreshFolders();
    }

    void OpenOutput_Click(object sender, RoutedEventArgs e) => OpenFolder(_settings.OutputFolder);

    void ChangeSource_Click(object sender, RoutedEventArgs e)
    {
        string? folder = PickFolder("녹화 폴더 선택", _settings.SourceFolder);
        if (folder == null) return;
        _settings.SourceFolder = folder;
        _settings.Save();
        SourceChanged = true;
        RefreshFolders();
    }

    string? PickFolder(string title, string current)
    {
        var dialog = new OpenFolderDialog { Title = title };
        if (Directory.Exists(current)) dialog.InitialDirectory = current;
        return dialog.ShowDialog(this) == true ? dialog.FolderName : null;
    }

    void OpenFolder(string folder)
    {
        try
        {
            Directory.CreateDirectory(folder);
            Process.Start(new ProcessStartInfo("explorer.exe", $"\"{folder}\"") { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, "폴더를 열 수 없습니다: " + ex.Message, "설정", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    // ───────── 임시 파일 ─────────

    void OpenTemp_Click(object sender, RoutedEventArgs e) => OpenFolder(AppPaths.TempFolder);

    void CleanTemp_Click(object sender, RoutedEventArgs e)
    {
        _releasePreview(); // 재생 중인 미리보기 파일도 지울 수 있게 미리보기 창을 닫는다
        int failed = AppPaths.CleanTemp();
        RefreshTemp();
        if (failed > 0)
            MessageBox.Show(this, $"사용 중인 파일 {failed}개는 지우지 못했습니다. 잠시 뒤 다시 시도해 주세요.",
                "임시 파일 정리", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    // ───────── 변환 엔진 ─────────

    void GpuSwitch_Click(object sender, RoutedEventArgs e)
    {
        _settings.UseGpu = GpuSwitch.IsChecked == true;
        _settings.Save();
    }

    void ChangeFfmpeg_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title = "ffmpeg.exe 선택",
            Filter = "ffmpeg.exe|ffmpeg.exe|실행 파일 (*.exe)|*.exe",
        };
        if (dialog.ShowDialog(this) != true) return;

        string folder = Path.GetDirectoryName(dialog.FileName)!;
        if (!File.Exists(Path.Combine(folder, "ffprobe.exe")))
        {
            MessageBox.Show(this, "같은 폴더에 ffprobe.exe가 없습니다.\nFFmpeg 배포판의 bin 폴더에 있는 ffmpeg.exe를 선택해 주세요.",
                "FFmpeg 위치", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        _settings.FfmpegFolder = folder;
        _settings.Save();
        FfmpegChanged = true;
        FfmpegText.Text = "FFmpeg: " + Path.Combine(folder, "ffmpeg.exe") + " (닫으면 다시 확인합니다)";
    }

    void Close_Click(object sender, RoutedEventArgs e) => Close();
}
