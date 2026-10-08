using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Media;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shell;
using Microsoft.Win32;
using TimelapseConverter.Helpers;
using TimelapseConverter.Services;

namespace TimelapseConverter;

public sealed record SourceFile(string Path, string Name, long Size, DateTime Modified)
{
    public string Meta => $"{Format.Size(Size)} · {Modified:yyyy-MM-dd HH:mm}";
}

public partial class MainWindow : Window
{
    static readonly string[] VideoExtensions = { ".mkv", ".mp4", ".mov" };
    const double SliderThumbWidth = 22; // Porcelain.xaml의 SliderThumb 크기와 같아야 함

    readonly AppSettings _settings = AppSettings.Load();
    readonly ObservableCollection<SourceFile> _files = new();
    readonly bool _ready;

    FfmpegTools? _tools;
    bool _gpuAvailable;
    VideoInfo? _video;
    int _probeVersion;            // 다른 파일을 연달아 눌렀을 때 늦게 끝난 분석 결과는 버림
    bool _suppressSelection;
    CancellationTokenSource? _cts;
    bool _closeAfterJob;
    string? _lastOutputPath;
    PreviewWindow? _previewWindow;

    int Speed => TimelapsePlanner.SpeedSteps[(int)SpeedSlider.Value];
    bool IsBusy => _cts != null;

    public MainWindow()
    {
        InitializeComponent();
        FileList.ItemsSource = _files;

        // 작은 화면에서도 창이 잘리지 않게
        var work = SystemParameters.WorkArea;
        if (Height > work.Height) Height = work.Height;
        if (Width > work.Width) Width = work.Width;

        SpeedSlider.Value = Math.Clamp(_settings.SpeedIndex, 0, TimelapsePlanner.SpeedSteps.Length - 1);
        if (DevSnapshot.Source != null) _settings.SourceFolder = DevSnapshot.Source;
        _ready = true;

        Loaded += OnLoaded;
        Closing += OnClosing;
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        TitleBar.Apply(this);
    }

    async void OnLoaded(object sender, RoutedEventArgs e)
    {
        BuildTicks();
        RefreshFileList();
        UpdateSpeedUi();
        UpdateButtons();
        await InitEngineAsync();

        if (DevSnapshot.Folder != null) await SaveSnapshotsAndExitAsync();
    }

    async Task SaveSnapshotsAndExitAsync()
    {
        await Task.Delay(400);
        DevSnapshot.Save(this, "start");

        if (DevSnapshot.File != null)
        {
            if (DevSnapshot.ConvertSpeedIndex is int index) SpeedSlider.Value = index;
            await SelectExternalAsync(DevSnapshot.File);
            await Task.Delay(400);
            DevSnapshot.Save(this, "main");

            DropOverlay.Visibility = Visibility.Visible;
            await Task.Delay(200);
            DevSnapshot.Save(this, "drop");
            DropOverlay.Visibility = Visibility.Collapsed;
        }

        if (DevSnapshot.ConvertOutput != null && _video != null)
        {
            _settings.OutputFolder = DevSnapshot.ConvertOutput;
            UpdateOutputUi();
            string target = Path.Combine(_settings.OutputFolder, OutputFileName(_video.Path, Speed));
            if (File.Exists(target)) File.Delete(target);

            Convert_Click(this, new RoutedEventArgs());
            bool captured = false;
            while (IsBusy)
            {
                if (!captured && Progress.Value >= 35)
                {
                    DevSnapshot.Save(this, "progress");
                    captured = true;
                }
                await Task.Delay(100);
            }
            await Task.Delay(300);
            DevSnapshot.Save(this, "result");

            Preview_Click(this, new RoutedEventArgs());
            await Task.Delay(300);
            while (IsBusy) await Task.Delay(200);
            await Task.Delay(1500);
            if (_previewWindow != null) DevSnapshot.Save(_previewWindow, "preview");
        }

        var settings = new SettingsWindow(_settings, _tools, _gpuAvailable, () => { }) { Owner = this };
        settings.Show();
        await Task.Delay(400);
        DevSnapshot.Save(settings, "settings");
        Application.Current.Shutdown();
    }

    // ───────────────────────── FFmpeg 준비 ─────────────────────────

    async Task InitEngineAsync()
    {
        _tools = FfmpegTools.Locate(_settings.FfmpegFolder);
        if (_tools == null)
        {
            _gpuAvailable = false;
            SetEngineStatus("FFmpeg 없음 · 클릭해서 지정", (Brush)FindResource("DangerBrush"),
                "ffmpeg.exe를 찾지 못했습니다.\n[설정]에서 ffmpeg.exe 위치를 지정해 주세요.\n(ffprobe.exe가 같은 폴더에 있어야 합니다)");
            UpdateButtons();
            return;
        }

        SetEngineStatus("FFmpeg 확인 중…", (Brush)FindResource("TextMutedBrush"), _tools.FfmpegPath);
        _gpuAvailable = await _tools.CheckNvencAsync();
        UpdateEngineText();
        UpdateButtons();

        // FFmpeg가 늦게 준비된 경우 이미 고른 파일을 다시 분석
        if (_video == null && FileList.SelectedItem is SourceFile f) await SelectVideoAsync(f.Path);
    }

    void SetEngineStatus(string text, Brush dot, string tooltip)
    {
        EngineText.Text = text;
        EngineDot.Fill = dot;
        EnginePill.ToolTip = tooltip;
    }

    void EnginePill_Click(object sender, MouseButtonEventArgs e) => OpenSettings();

    void Settings_Click(object sender, RoutedEventArgs e) => OpenSettings();

    async void OpenSettings()
    {
        if (IsBusy) return;
        var window = new SettingsWindow(_settings, _tools, _gpuAvailable, () => _previewWindow?.Close()) { Owner = this };
        window.ShowDialog();

        if (window.SourceChanged) RefreshFileList();
        if (window.OutputChanged) UpdateOutputUi();
        UpdateEngineText();
        if (window.FfmpegChanged) await InitEngineAsync();
    }

    /// <summary>GPU 사용 여부(설정)에 맞춰 헤더 상태 표시 갱신</summary>
    void UpdateEngineText()
    {
        if (_tools == null) return;
        if (_gpuAvailable && _settings.UseGpu)
            SetEngineStatus("NVIDIA GPU 가속", (Brush)FindResource("SuccessBrush"),
                $"FFmpeg: {_tools.FfmpegPath}\nGPU 사용 여부는 [설정]에서 바꿀 수 있습니다.");
        else
            SetEngineStatus("CPU 모드", (Brush)FindResource("WarningBrush"),
                $"FFmpeg: {_tools.FfmpegPath}\n" + (_gpuAvailable
                    ? "[설정]에서 GPU 가속이 꺼져 있습니다."
                    : "NVIDIA 하드웨어 인코더를 사용할 수 없어 CPU로 변환합니다."));
    }

    // ───────────────────────── 파일 목록 / 선택 ─────────────────────────

    void RefreshFileList()
    {
        string folder = _settings.SourceFolder;
        SourceFolderText.Text = folder;
        SourceFolderText.ToolTip = folder;

        _suppressSelection = true;
        _files.Clear();
        bool exists = Directory.Exists(folder);
        try
        {
            if (exists)
            {
                var items = new DirectoryInfo(folder).EnumerateFiles()
                    .Where(f => IsVideoFile(f.Name))
                    .OrderByDescending(f => f.LastWriteTime)
                    .Select(f => new SourceFile(f.FullName, f.Name, f.Length, f.LastWriteTime));
                foreach (var item in items) _files.Add(item);
            }
        }
        catch (Exception ex)
        {
            ShowMessage("녹화 폴더를 읽을 수 없습니다: " + ex.Message, isError: true);
        }

        // 선택했던 파일이 목록에 있으면 선택 표시 유지
        if (_video != null)
            FileList.SelectedItem = _files.FirstOrDefault(f => SamePath(f.Path, _video.Path));
        _suppressSelection = false;

        EmptyListText.Text = exists
            ? "이 폴더에 영상 파일(mkv, mp4, mov)이 없습니다"
            : "녹화 폴더를 찾을 수 없습니다.\n위의 [변경]을 눌러 폴더를 지정해 주세요.";
        EmptyListText.Visibility = _files.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    void RefreshFiles_Click(object sender, RoutedEventArgs e) => RefreshFileList();

    void ChangeSourceFolder_Click(object sender, RoutedEventArgs e)
    {
        string? folder = PickFolder("녹화 폴더 선택", _settings.SourceFolder);
        if (folder == null) return;
        _settings.SourceFolder = folder;
        _settings.Save();
        RefreshFileList();
    }

    async void FileList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressSelection || FileList.SelectedItem is not SourceFile file) return;
        await SelectVideoAsync(file.Path);
    }

    async void OpenFile_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title = "변환할 영상 선택",
            Filter = "영상 파일 (*.mkv;*.mp4;*.mov)|*.mkv;*.mp4;*.mov|모든 파일 (*.*)|*.*",
            InitialDirectory = Directory.Exists(_settings.SourceFolder) ? _settings.SourceFolder : "",
        };
        if (dialog.ShowDialog(this) == true) await SelectExternalAsync(dialog.FileName);
    }

    /// <summary>목록 밖(파일 열기, 드래그 앤 드롭)에서 고른 파일</summary>
    async Task SelectExternalAsync(string path)
    {
        _suppressSelection = true;
        FileList.SelectedItem = _files.FirstOrDefault(f => SamePath(f.Path, path));
        _suppressSelection = false;
        await SelectVideoAsync(path);
    }

    async Task SelectVideoAsync(string path)
    {
        if (IsBusy) return;
        int version = ++_probeVersion;

        _video = null;
        HideMessage();
        ResultPanel.Visibility = Visibility.Collapsed;
        SetSelectedInfo(Path.GetFileName(path), "영상 정보를 분석하는 중…", ok: true);
        UpdateSpeedUi();
        UpdateButtons();

        if (_tools == null)
        {
            SetSelectedInfo(Path.GetFileName(path),
                "FFmpeg를 찾을 수 없어 영상을 분석하지 못했습니다. 오른쪽 위 상태 표시를 눌러 ffmpeg.exe 위치를 지정해 주세요.", ok: false);
            return;
        }

        try
        {
            var info = await _tools.ProbeAsync(path, CancellationToken.None);
            if (version != _probeVersion) return;
            _video = info;

            // 한글은 줄바꿈이 글자 단위로 끊기므로 두 줄로 나눠서 표시
            string gop = info.KeyframeIntervalSec > 0 ? $" · 키프레임 {info.KeyframeIntervalSec:0.#}초 간격" : "";
            SetSelectedInfo(info.FileName,
                $"길이 {Format.Duration(info.DurationSec)} · {info.Width}×{info.Height} · {info.Fps:0.##}fps\n{Format.Size(info.SizeBytes)}{gop}",
                ok: true);
        }
        catch (Exception ex)
        {
            if (version != _probeVersion) return;
            SetSelectedInfo(Path.GetFileName(path), "영상 정보를 읽지 못했습니다. " + ex.Message, ok: false);
        }

        UpdateSpeedUi();
        UpdateButtons();
    }

    void SetSelectedInfo(string name, string meta, bool ok)
    {
        SelectedInfo.Visibility = Visibility.Visible;
        SelectedInfo.Background = (Brush)FindResource(ok ? "SuccessSoftBrush" : "DangerSoftBrush");
        SelectedInfo.BorderBrush = (Brush)FindResource(ok ? "SuccessLineBrush" : "DangerLineBrush");
        SelectedIconBg.Background = (Brush)FindResource(ok ? "SuccessBrush" : "DangerBrush");
        SelectedIcon.Text = ok ? "\uE73E" : "\uE711";
        SelectedName.Text = name;
        SelectedName.ToolTip = name;
        SelectedMeta.Text = meta;
    }

    // ───────────────────────── 배속 ─────────────────────────

    void SpeedSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (!_ready) return;
        _settings.SpeedIndex = (int)SpeedSlider.Value;
        UpdateSpeedUi();
    }

    void BuildTicks()
    {
        TickCanvas.Children.Clear();
        for (int i = 0; i < TimelapsePlanner.SpeedSteps.Length; i++)
        {
            var label = new TextBlock
            {
                Text = "x" + TimelapsePlanner.SpeedSteps[i],
                FontSize = 10.5,
                Cursor = Cursors.Hand,
                Tag = i,
            };
            label.MouseLeftButtonUp += (s, _) =>
            {
                if (SpeedSlider.IsEnabled) SpeedSlider.Value = (int)((TextBlock)s).Tag;
            };
            TickCanvas.Children.Add(label);
        }
        LayoutTicks();
        HighlightTick();
    }

    void TickCanvas_SizeChanged(object sender, SizeChangedEventArgs e) => LayoutTicks();

    /// <summary>슬라이더 각 단계(thumb 중심) 바로 아래에 라벨을 맞춰 배치</summary>
    void LayoutTicks()
    {
        double width = TickCanvas.ActualWidth;
        int count = TickCanvas.Children.Count;
        if (width <= 0 || count < 2) return;

        double usable = width - SliderThumbWidth;
        bool crowded = usable / (count - 1) < 36; // 좁으면 한 칸씩 건너 표시
        for (int i = 0; i < count; i++)
        {
            var label = (TextBlock)TickCanvas.Children[i];
            label.Visibility = crowded && i % 2 == 1 ? Visibility.Hidden : Visibility.Visible;
            label.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            double center = SliderThumbWidth / 2 + usable * i / (count - 1);
            Canvas.SetLeft(label, center - label.DesiredSize.Width / 2);
        }
    }

    void HighlightTick()
    {
        int current = (int)SpeedSlider.Value;
        var accent = (Brush)FindResource("AccentBrush");
        var muted = (Brush)FindResource("TextMutedBrush");
        foreach (TextBlock label in TickCanvas.Children)
        {
            bool on = (int)label.Tag == current;
            label.Foreground = on ? accent : muted;
            label.FontWeight = on ? FontWeights.Bold : FontWeights.Normal;
        }
    }

    void UpdateSpeedUi()
    {
        if (!_ready) return;
        int speed = Speed;
        SpeedBadgeText.Text = "x" + speed;
        HighlightTick();

        if (_video == null)
        {
            OriginalDurationText.Text = "--";
            OutputDurationText.Text = "--";
            EstimateRow.Visibility = Visibility.Collapsed;
        }
        else
        {
            var plan = TimelapsePlanner.Plan(_video, speed);
            OriginalDurationText.Text = Format.Duration(_video.DurationSec);
            OutputDurationText.Text = Format.Duration(plan.OutputSeconds);
            EstimateText.Text = "약 " + Format.Duration(plan.EstimatedSeconds);
            ModeText.Text = TimelapsePlanner.ModeLabel(plan.Mode);
            ModeChip.ToolTip = TimelapsePlanner.ModeDescription(plan.Mode, _video.KeyframeIntervalSec);
            EstimateRow.Visibility = Visibility.Visible;
        }
        UpdateOutputUi();
    }

    // ───────────────────────── 저장 위치 ─────────────────────────

    void UpdateOutputUi()
    {
        OutputFolderText.Text = _settings.OutputFolder;
        OutputFolderText.ToolTip = _settings.OutputFolder;
        OutputNameText.Text = _video == null ? "영상을 선택하면 표시됩니다" : OutputFileName(_video.Path, Speed);
        OutputNameText.ToolTip = OutputNameText.Text;
    }

    // 웹 버전과 같은 규칙: 원본파일명_배속x.mp4
    static string OutputFileName(string input, int speed) =>
        $"{Path.GetFileNameWithoutExtension(input)}_{speed}x.mp4";

    void ChangeOutputFolder_Click(object sender, RoutedEventArgs e)
    {
        string? folder = PickFolder("타임랩스 저장 폴더 선택", _settings.OutputFolder);
        if (folder == null) return;
        _settings.OutputFolder = folder;
        _settings.Save();
        UpdateOutputUi();
    }

    void OpenOutputFolder_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            Directory.CreateDirectory(_settings.OutputFolder);
            Process.Start(new ProcessStartInfo("explorer.exe", $"\"{_settings.OutputFolder}\"") { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            ShowMessage("저장 폴더를 열 수 없습니다: " + ex.Message, isError: true);
        }
    }

    string? PickFolder(string title, string current)
    {
        var dialog = new OpenFolderDialog { Title = title };
        if (Directory.Exists(current)) dialog.InitialDirectory = current;
        return dialog.ShowDialog(this) == true ? dialog.FolderName : null;
    }

    // ───────────────────────── 미리보기 / 변환 ─────────────────────────

    async void Preview_Click(object sender, RoutedEventArgs e)
    {
        if (_video == null || _tools == null || IsBusy) return;
        var video = _video;
        int speed = Speed;

        double sample = TimelapsePlanner.PreviewSourceSeconds(video, speed);
        var plan = TimelapsePlanner.Plan(video, speed, sample);
        string target = Path.Combine(AppPaths.TempFolder, $"preview_{DateTime.Now:HHmmssfff}.mp4");

        _previewWindow?.Close();
        var result = await RunJobAsync("미리보기 생성", video, plan, target, preview: true);
        if (result == null) return;

        string range = sample >= video.DurationSec ? "전체" : $"앞 {Format.Duration(sample)}";
        _previewWindow = new PreviewWindow(target,
            $"x{speed} · 원본 {range} 구간 → {Format.Duration(plan.OutputSeconds)} · {TimelapsePlanner.ModeLabel(plan.Mode)}")
        {
            Owner = this,
        };
        _previewWindow.Closed += (_, _) => _previewWindow = null;
        _previewWindow.Show();
    }

    async void Convert_Click(object sender, RoutedEventArgs e)
    {
        if (_video == null || _tools == null || IsBusy) return;
        var video = _video;
        int speed = Speed;

        string folder = _settings.OutputFolder;
        try
        {
            Directory.CreateDirectory(folder);
        }
        catch (Exception ex)
        {
            ShowMessage($"저장 폴더를 만들 수 없습니다: {ex.Message}", isError: true);
            return;
        }

        string target = Path.Combine(folder, OutputFileName(video.Path, speed));
        if (SamePath(target, video.Path))
        {
            ShowMessage("원본 파일과 같은 경로에는 저장할 수 없습니다. 저장 위치를 바꿔 주세요.", isError: true);
            return;
        }
        if (File.Exists(target))
        {
            var answer = MessageBox.Show(this,
                $"같은 이름의 파일이 이미 있습니다.\n\n{Path.GetFileName(target)}\n\n" +
                "[예] 덮어쓰기\n[아니요] 새 이름으로 저장\n[취소] 그만두기",
                "파일이 이미 있음", MessageBoxButton.YesNoCancel, MessageBoxImage.Question);
            if (answer == MessageBoxResult.Cancel) return;
            if (answer == MessageBoxResult.No) target = UniquePath(target);
        }

        var plan = TimelapsePlanner.Plan(video, speed);
        var result = await RunJobAsync("변환", video, plan, target, preview: false);
        if (result == null) return;

        _lastOutputPath = target;
        long size = File.Exists(target) ? new FileInfo(target).Length : 0;
        ResultTitle.Text = result.UsedGpu ? "변환 완료" : "변환 완료 (CPU)";
        ResultPath.Text = target;
        ResultPath.ToolTip = target;
        ResultMeta.Text = $"x{speed} · {Format.Duration(plan.OutputSeconds)} · {Format.Size(size)} · 걸린 시간 {Format.Clock(result.Elapsed)}";
        ResultPanel.Visibility = Visibility.Visible;
        SystemSounds.Asterisk.Play();

        if (SamePath(folder, _settings.SourceFolder)) RefreshFileList();
    }

    sealed record JobResult(bool UsedGpu, TimeSpan Elapsed);

    /// <summary>미리보기·변환 공통 실행: 진행률 표시, 취소, 오류 처리</summary>
    async Task<JobResult?> RunJobAsync(string verb, VideoInfo video, ConversionPlan plan, string target, bool preview)
    {
        _cts = new CancellationTokenSource();
        var token = _cts.Token;
        var total = Stopwatch.StartNew();
        var segment = Stopwatch.StartNew(); // GPU 실패 후 CPU 재시도 시 남은 시간 계산을 새로 시작

        HideMessage();
        ResultPanel.Visibility = Visibility.Collapsed;
        ProgressPanel.Visibility = Visibility.Visible;
        ProgressNoteText.Visibility = Visibility.Collapsed;
        Progress.Value = 0;
        ProgressText.Text = $"{verb} 준비 중…";
        ProgressTimeText.Text = $"예상 약 {Format.Duration(plan.EstimatedSeconds)}";
        Taskbar.ProgressState = TaskbarItemProgressState.Normal;
        Taskbar.ProgressValue = 0;
        UpdateButtons();

        var progress = new Progress<ConvertProgress>(p =>
        {
            if (p.Note != null)
            {
                ProgressNoteText.Text = p.Note;
                ProgressNoteText.Visibility = Visibility.Visible;
                segment.Restart();
            }
            Progress.Value = p.Percent;
            Taskbar.ProgressValue = p.Percent / 100;
            ProgressText.Text = $"{verb} 중… {p.Percent:0}%";

            string time = "경과 " + Format.Clock(total.Elapsed);
            if (p.Percent >= 3)
            {
                double remain = segment.Elapsed.TotalSeconds * (100 - p.Percent) / p.Percent;
                time += " · 남은 시간 약 " + Format.Clock(TimeSpan.FromSeconds(remain));
            }
            ProgressTimeText.Text = time;
        });

        bool useGpu = _gpuAvailable && _settings.UseGpu;
        try
        {
            bool usedGpu = await _tools!.ConvertAsync(video.Path, target, plan, useGpu, preview, progress, token);
            return new JobResult(usedGpu, total.Elapsed);
        }
        catch (OperationCanceledException)
        {
            ShowMessage($"{verb} 작업을 취소했습니다.", isError: false);
            return null;
        }
        catch (FfmpegException ex)
        {
            ShowMessage($"{ex.Message}\n{LastLines(ex.Detail, 4)}", isError: true, detail: ex.Detail);
            return null;
        }
        catch (Exception ex)
        {
            ShowMessage($"{verb} 중 오류가 발생했습니다: {ex.Message}", isError: true);
            return null;
        }
        finally
        {
            _cts.Dispose();
            _cts = null;
            ProgressPanel.Visibility = Visibility.Collapsed;
            Taskbar.ProgressState = TaskbarItemProgressState.None;
            UpdateButtons();
            if (_closeAfterJob) _ = Dispatcher.InvokeAsync(Close);
        }
    }

    void Cancel_Click(object sender, RoutedEventArgs e) => _cts?.Cancel();

    void RevealOutput_Click(object sender, RoutedEventArgs e)
    {
        if (_lastOutputPath == null || !File.Exists(_lastOutputPath)) return;
        Process.Start("explorer.exe", $"/select,\"{_lastOutputPath}\"");
    }

    void PlayOutput_Click(object sender, RoutedEventArgs e)
    {
        if (_lastOutputPath == null || !File.Exists(_lastOutputPath)) return;
        try { Process.Start(new ProcessStartInfo(_lastOutputPath) { UseShellExecute = true }); }
        catch (Exception ex) { ShowMessage("영상을 열 수 없습니다: " + ex.Message, isError: true); }
    }

    // ───────────────────────── 드래그 앤 드롭 ─────────────────────────

    static string? DroppedVideo(DragEventArgs e) =>
        e.Data.GetData(DataFormats.FileDrop) is string[] { Length: > 0 } files && IsVideoFile(files[0]) ? files[0] : null;

    void Window_DragEnter(object sender, DragEventArgs e)
    {
        bool ok = !IsBusy && DroppedVideo(e) != null;
        e.Effects = ok ? DragDropEffects.Copy : DragDropEffects.None;
        DropOverlay.Visibility = ok ? Visibility.Visible : Visibility.Collapsed;
        e.Handled = true;
    }

    void Window_DragOver(object sender, DragEventArgs e)
    {
        e.Effects = !IsBusy && DroppedVideo(e) != null ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    void Window_DragLeave(object sender, DragEventArgs e) => DropOverlay.Visibility = Visibility.Collapsed;

    async void Window_Drop(object sender, DragEventArgs e)
    {
        DropOverlay.Visibility = Visibility.Collapsed;
        string? path = DroppedVideo(e);
        if (path == null || IsBusy) return;
        Activate();
        await SelectExternalAsync(path);
    }

    // ───────────────────────── 공통 UI ─────────────────────────

    void UpdateButtons()
    {
        bool busy = IsBusy;
        bool ready = _video != null && _tools != null && !busy;

        PreviewButton.IsEnabled = ready;
        ConvertButton.IsEnabled = ready;
        PreviewButton.Visibility = busy ? Visibility.Collapsed : Visibility.Visible;
        ConvertButton.Visibility = busy ? Visibility.Collapsed : Visibility.Visible;
        CancelButton.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;

        FileList.IsEnabled = !busy;
        OpenFileButton.IsEnabled = !busy;
        ChangeSourceButton.IsEnabled = !busy;
        RefreshButton.IsEnabled = !busy;
        ChangeOutputButton.IsEnabled = !busy;
        SpeedSlider.IsEnabled = !busy;
        SettingsButton.IsEnabled = !busy;
    }

    void ShowMessage(string text, bool isError, string? detail = null)
    {
        MessagePanel.Background = (Brush)FindResource(isError ? "DangerSoftBrush" : "SurfaceAltBrush");
        MessagePanel.BorderBrush = (Brush)FindResource(isError ? "DangerLineBrush" : "LineBrush");
        MessageText.Foreground = (Brush)FindResource(isError ? "DangerBrush" : "TextSecondaryBrush");
        MessageText.Text = text.Trim();
        MessagePanel.ToolTip = string.IsNullOrWhiteSpace(detail) ? null : detail;
        MessagePanel.Visibility = Visibility.Visible;
    }

    void HideMessage() => MessagePanel.Visibility = Visibility.Collapsed;

    void OnClosing(object? sender, CancelEventArgs e)
    {
        if (IsBusy)
        {
            if (_closeAfterJob)
            {
                e.Cancel = true;
                return;
            }
            var answer = MessageBox.Show(this, "작업이 진행 중입니다.\n중단하고 종료할까요?\n\n(만들던 파일은 삭제됩니다)",
                "종료 확인", MessageBoxButton.YesNo, MessageBoxImage.Warning);
            e.Cancel = true;
            if (answer == MessageBoxResult.Yes)
            {
                // FFmpeg를 멈추고 임시 파일을 지운 뒤 RunJobAsync가 창을 닫는다
                _closeAfterJob = true;
                _cts?.Cancel();
            }
            return;
        }

        if (DevSnapshot.Folder == null) _settings.Save(); // 검증 실행은 사용자 설정을 건드리지 않음
        _previewWindow?.Close();
    }

    // ───────────────────────── 유틸 ─────────────────────────

    static bool IsVideoFile(string path) =>
        VideoExtensions.Contains(Path.GetExtension(path).ToLowerInvariant());

    static bool SamePath(string a, string b)
    {
        try
        {
            return string.Equals(Path.GetFullPath(a).TrimEnd('\\'), Path.GetFullPath(b).TrimEnd('\\'),
                StringComparison.OrdinalIgnoreCase);
        }
        catch { return false; }
    }

    static string UniquePath(string path)
    {
        string dir = Path.GetDirectoryName(path)!;
        string name = Path.GetFileNameWithoutExtension(path);
        string ext = Path.GetExtension(path);
        for (int i = 2; ; i++)
        {
            string candidate = Path.Combine(dir, $"{name} ({i}){ext}");
            if (!File.Exists(candidate)) return candidate;
        }
    }

    static string LastLines(string text, int count) =>
        string.Join("\n", text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).TakeLast(count));
}
