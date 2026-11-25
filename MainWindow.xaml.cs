using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Media;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shell;
using System.Windows.Threading;
using Microsoft.Win32;
using TimelapseConverter.Helpers;
using TimelapseConverter.Services;

namespace TimelapseConverter;

public sealed class SourceFile : INotifyPropertyChanged
{
    double _duration;

    public SourceFile(FileInfo file)
    {
        Path = file.FullName;
        Name = file.Name;
        Size = file.Length;
        Modified = file.LastWriteTime;
        CacheKey = MainWindow.CacheKeyOf(file);
    }

    public string Path { get; }
    public string Name { get; }
    public long Size { get; }
    public DateTime Modified { get; }
    public string CacheKey { get; }

    /// <summary>영상 길이(초). 목록을 띄운 뒤 백그라운드에서 채워진다 (0 = 아직 모름)</summary>
    public double Duration
    {
        get => _duration;
        set
        {
            _duration = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Meta)));
        }
    }

    public string Meta =>
        (Duration > 0 ? Format.Duration(Duration) + " · " : "") + $"{Format.Size(Size)} · {Modified:yyyy-MM-dd HH:mm}";

    public event PropertyChangedEventHandler? PropertyChanged;
}

public partial class MainWindow : Window
{
    static readonly string[] VideoExtensions = { ".mkv", ".mp4", ".mov" };
    const double SliderThumbWidth = 22; // Porcelain.xaml의 SliderThumb 크기와 같아야 함
    const string TrimHint = "녹화 앞뒤의 필요 없는 부분을 잘라낼 수 있습니다";

    // 분석 결과 캐시: 경로·크기·수정 시각이 같으면 다시 분석하지 않는다
    static readonly Dictionary<string, VideoInfo> ProbeCache = new();
    static readonly Dictionary<string, double> DurationCache = new();

    readonly AppSettings _settings = AppSettings.Load();
    readonly ObservableCollection<SourceFile> _files = new();
    readonly CompletionNotifier _notifier;
    readonly DispatcherTimer _frameTimer = new() { Interval = TimeSpan.FromMilliseconds(180) };
    readonly bool _ready;

    FfmpegTools? _tools;
    bool _gpuAvailable;

    List<VideoInfo> _selection = new();   // 분석이 끝난 선택 영상 (2개 이상이면 한꺼번에 변환)
    int _probeVersion;                    // 선택을 연달아 바꿨을 때 늦게 끝난 분석 결과는 버림
    CancellationTokenSource? _probeCts;
    CancellationTokenSource? _durationCts;
    bool _suppressSelection;
    bool _suppressSlider;

    double _trimStart, _trimEnd;          // 파일 하나를 골랐을 때 변환 구간(초)
    bool _startFrameDirty, _endFrameDirty;
    CancellationTokenSource? _startFrameCts, _endFrameCts;

    CancellationTokenSource? _cts;
    bool _closeAfterJob;
    string? _lastOutputPath;
    PreviewWindow? _previewWindow;

    VideoInfo? Single => _selection.Count == 1 ? _selection[0] : null;
    bool IsBatch => _selection.Count > 1;
    bool IsBusy => _cts != null;
    int? Target => _settings.TargetSeconds;
    int ManualSpeed => TimelapsePlanner.SpeedSteps[Math.Clamp((int)Math.Round(SpeedSlider.Value), 0, TimelapsePlanner.SpeedSteps.Length - 1)];
    bool HasTrim => Single is VideoInfo v && (_trimStart > 0.5 || _trimEnd < v.DurationSec - 0.5);

    public MainWindow()
    {
        InitializeComponent();
        FileList.ItemsSource = _files;
        _notifier = new CompletionNotifier(this);

        // 작은 화면에서도 창이 잘리지 않게
        var work = SystemParameters.WorkArea;
        if (Height > work.Height) Height = work.Height;
        if (Width > work.Width) Width = work.Width;

        SpeedSlider.Value = Math.Clamp(_settings.SpeedIndex, 0, TimelapsePlanner.SpeedSteps.Length - 1);
        SpeedSlider.PreviewMouseLeftButtonDown += (_, _) => LeaveTargetMode();
        if (_settings.TargetSeconds is int t && !TimelapsePlanner.TargetSteps.Contains(t)) _settings.TargetSeconds = null;
        if (DevSnapshot.Source != null) _settings.SourceFolder = DevSnapshot.Source;
        if (DevSnapshot.Target != null) _settings.TargetSeconds = DevSnapshot.Target;
        BuildTargetChips();

        TrimBar.RangeChanging += OnTrimChanging;
        TrimBar.RangeCommitted += isStart => ScheduleFrame(isStart, immediate: true);
        _frameTimer.Tick += (_, _) =>
        {
            _frameTimer.Stop();
            RefreshFrames();
        };
        _ready = true;

        Loaded += OnLoaded;
        Closing += OnClosing;
        Closed += (_, _) => _notifier.Dispose();
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
        UpdateAll();
        await InitEngineAsync();

        if (DevSnapshot.Folder != null) await SaveSnapshotsAndExitAsync();
    }

    async Task SaveSnapshotsAndExitAsync()
    {
        // 캡처하는 동안 실제 마우스 위치 때문에 목록 항목 등에 강조 표시가 생기지 않도록
        ((UIElement)Content).IsHitTestVisible = false;
        Mouse.Synchronize();
        await Task.Delay(400);
        DevSnapshot.Save(this, "start");

        if (DevSnapshot.File != null)
        {
            if (DevSnapshot.ConvertSpeedIndex is int index) SpeedSlider.Value = index;
            if (DevSnapshot.Target != null) _settings.TargetSeconds = DevSnapshot.Target;
            await SelectExternalAsync([DevSnapshot.File]);
            if (DevSnapshot.Trim is (double start, double end) && Single is VideoInfo v)
            {
                _trimStart = start;
                _trimEnd = Math.Min(end, v.DurationSec);
                TrimBar.SetRange(v.DurationSec, _trimStart, _trimEnd);
                ScheduleFrame(true, immediate: true);
                ScheduleFrame(false, immediate: true);
                UpdateAll();
            }
            await Task.Delay(1500); // 구간 미리보기 이미지
            if (DevSnapshot.SelfTest)
            {
                RunSelfTest();
                Application.Current.Shutdown();
                return;
            }
            DevSnapshot.Save(this, "main");

            DropOverlay.Visibility = Visibility.Visible;
            await Task.Delay(200);
            DevSnapshot.Save(this, "drop");
            DropOverlay.Visibility = Visibility.Collapsed;
        }

        if (DevSnapshot.ConvertOutput != null && Single is VideoInfo video)
        {
            _settings.OutputFolder = DevSnapshot.ConvertOutput;
            UpdateOutputUi();
            string target = Path.Combine(_settings.OutputFolder, OutputFileName(video.Path));
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
            _previewWindow?.Close();
        }

        if (DevSnapshot.Batch && _files.Count >= 2)
        {
            FileList.SelectAll();
            for (int i = 0; i < 100 && _selection.Count < 2; i++) await Task.Delay(100);
            await Task.Delay(400);
            DevSnapshot.Save(this, "batch");

            if (DevSnapshot.ConvertOutput != null)
            {
                foreach (var v in _selection)
                {
                    string target = Path.Combine(_settings.OutputFolder, OutputFileName(v.Path));
                    if (File.Exists(target)) File.Delete(target);
                }
                Convert_Click(this, new RoutedEventArgs());
                bool captured = false;
                while (IsBusy)
                {
                    if (!captured && Progress.Value >= 50)
                    {
                        DevSnapshot.Save(this, "batch-progress");
                        captured = true;
                    }
                    await Task.Delay(100);
                }
                await Task.Delay(300);
                DevSnapshot.Save(this, "batch-result");
            }
        }

        if (DevSnapshot.NotifyTest)
        {
            // 알림 표시 확인용: 결과를 파일로 남기고 알림이 보이도록 잠시 기다린다
            bool shown = _notifier.Show("변환 완료", "알림 표시 테스트입니다 · TimeLapse Converter");
            File.WriteAllText(Path.Combine(DevSnapshot.Folder!, "notify.txt"), $"shown={shown}");
            await Task.Delay(6000);
        }

        var settings = new SettingsWindow(_settings, _tools, _gpuAvailable, () => { }) { Owner = this };
        settings.Show();
        await Task.Delay(400);
        DevSnapshot.Save(settings, "settings");
        Application.Current.Shutdown();
    }

    /// <summary>개발용: 목표 길이 칩·슬라이더·구간 입력 동작을 실제 이벤트 처리기로 확인하고 결과를 selftest.txt에 저장</summary>
    void RunSelfTest()
    {
        var log = new List<string>();
        void Check(string name, bool ok) => log.Add($"{(ok ? "PASS" : "FAIL")}  {name}");
        static void Click(ButtonBase b) => b.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
        ToggleButton Chip(int s) => TargetPanel.Children.OfType<ToggleButton>().First(c => (int)c.Tag == s);
        if (Single is not VideoInfo v) { log.Add("FAIL  no video"); goto done; }

        ResetTrim(v);
        _settings.TargetSeconds = null;
        SetSliderSilently(6);
        UpdateAll();
        Check("manual x64", SpeedBadgeText.Text == "x64" && OutputNameText.Text.EndsWith("_64x.mp4"));

        Click(Chip(15));
        Check("chip 15 on", Target == 15 && Chip(15).IsChecked == true
                            && SpeedBadgeText.Text == Format.Speed(v.DurationSec / 15)
                            && OutputNameText.Text.EndsWith("_15s.mp4"));
        Check("slider follows target", Math.Abs(SpeedSlider.Value - Math.Clamp(Math.Log2(v.DurationSec / 15), 0, 12)) < 0.001);
        Click(Chip(30));
        Check("chip switch 15→30", Target == 30 && Chip(15).IsChecked == false && Chip(30).IsChecked == true);
        Click(Chip(30));
        Check("chip 30 off → nearest step", Target == null && SpeedSlider.Value == Math.Round(SpeedSlider.Value)
                                           && Chip(30).IsChecked == false);
        Click(Chip(10));
        LeaveTargetMode();
        Check("slider press leaves target", Target == null && SpeedBadgeText.Text.StartsWith('x') && Chip(10).IsChecked == false);

        StartTimeBox.Text = "1:00";
        ApplyTimeBox(StartTimeBox);
        Check("start 1:00", _trimStart == 60 && HasTrim && TrimBar.Start == 60 && ResetTrimButton.Visibility == Visibility.Visible);
        EndTimeBox.Text = "abc";
        ApplyTimeBox(EndTimeBox);
        Check("invalid end ignored", Math.Abs(_trimEnd - v.DurationSec) < 0.01 && EndTimeBox.Text != "abc");
        EndTimeBox.Text = "0:30";
        ApplyTimeBox(EndTimeBox);
        Check("end before start clamped", _trimEnd > _trimStart && _trimEnd <= v.DurationSec);
        EndTimeBox.Text = "120";
        ApplyTimeBox(EndTimeBox);
        Check("end as seconds", _trimEnd == 120 && OriginalDurationText.Text == "1분" && OriginalLabel.Text == "구간 길이");
        Click(Chip(20));
        Check("target uses trimmed length", Math.Abs(PlanFor(v).Speed - 3) < 0.001 && PlanFor(v).StartSeconds == 60);
        Click(ResetTrimButton);
        Check("reset trim", !HasTrim && _trimStart == 0 && Math.Abs(_trimEnd - v.DurationSec) < 0.01);
        _settings.TargetSeconds = null;

    done:
        File.WriteAllLines(Path.Combine(DevSnapshot.Folder!, "selftest.txt"), log);
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
        LoadListDurations();
        _gpuAvailable = await _tools.CheckNvencAsync();
        UpdateEngineText();
        UpdateButtons();

        // FFmpeg가 늦게 준비된 경우 이미 고른 파일을 다시 분석
        if (_selection.Count == 0 && FileList.SelectedItems.Count > 0) await SelectVideosAsync(SelectedListPaths());
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

    // ───────────────────────── 파일 목록 ─────────────────────────

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
                    .Select(f => new SourceFile(f));
                foreach (var item in items) _files.Add(item);
            }
        }
        catch (Exception ex)
        {
            ShowMessage("녹화 폴더를 읽을 수 없습니다: " + ex.Message, isError: true);
        }

        // 선택했던 파일이 목록에 있으면 선택 표시 유지
        foreach (var f in _files.Where(f => _selection.Any(v => SamePath(v.Path, f.Path))))
            FileList.SelectedItems.Add(f);
        _suppressSelection = false;

        EmptyListText.Text = exists
            ? "이 폴더에 영상 파일(mkv, mp4, mov)이 없습니다"
            : "녹화 폴더를 찾을 수 없습니다.\n위의 [변경]을 눌러 폴더를 지정해 주세요.";
        EmptyListText.Visibility = _files.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        LoadListDurations();
    }

    /// <summary>목록의 영상 길이를 백그라운드에서 채운다 (길이만 읽는 가벼운 분석, 3개씩 동시에)</summary>
    async void LoadListDurations()
    {
        _durationCts?.Cancel();
        if (_tools == null) return;
        var tools = _tools;
        var cts = _durationCts = new CancellationTokenSource();

        var pending = new List<SourceFile>();
        foreach (var f in _files)
        {
            if (f.Duration > 0) continue;
            if (DurationCache.TryGetValue(f.CacheKey, out double known)) f.Duration = known;
            else pending.Add(f);
        }
        if (pending.Count == 0) return;

        using var gate = new SemaphoreSlim(3);
        async Task ProbeOne(SourceFile f)
        {
            try { await gate.WaitAsync(cts.Token); }
            catch { return; }
            try
            {
                double d = await tools.ProbeDurationAsync(f.Path, cts.Token);
                if (cts.IsCancellationRequested || d <= 0) return;
                DurationCache[f.CacheKey] = d;
                f.Duration = d;
            }
            catch { /* 녹화 중인 파일 등은 길이 없이 표시 */ }
            finally { gate.Release(); }
        }
        await Task.WhenAll(pending.Select(ProbeOne));
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

    // ───────────────────────── 선택 ─────────────────────────

    List<string> SelectedListPaths() =>
        FileList.SelectedItems.Cast<SourceFile>().OrderBy(f => _files.IndexOf(f)).Select(f => f.Path).ToList();

    async void FileList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressSelection) return;
        var paths = SelectedListPaths();
        if (paths.Count == 0)
        {
            ClearSelection();
            return;
        }
        await SelectVideosAsync(paths);
    }

    async void OpenFile_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title = "변환할 영상 선택 (여러 개 선택 가능)",
            Filter = "영상 파일 (*.mkv;*.mp4;*.mov)|*.mkv;*.mp4;*.mov|모든 파일 (*.*)|*.*",
            InitialDirectory = Directory.Exists(_settings.SourceFolder) ? _settings.SourceFolder : "",
            Multiselect = true,
        };
        if (dialog.ShowDialog(this) == true) await SelectExternalAsync(dialog.FileNames);
    }

    /// <summary>목록 밖(파일 열기, 드래그 앤 드롭)에서 고른 파일</summary>
    async Task SelectExternalAsync(IReadOnlyList<string> paths)
    {
        _suppressSelection = true;
        FileList.SelectedItems.Clear();
        foreach (var f in _files.Where(f => paths.Any(p => SamePath(p, f.Path)))) FileList.SelectedItems.Add(f);
        _suppressSelection = false;
        await SelectVideosAsync(paths);
    }

    void ClearSelection()
    {
        _probeVersion++;
        _probeCts?.Cancel();
        _selection = new();
        ResetTrim(null);
        SelectedInfo.Visibility = Visibility.Collapsed;
        UpdateAll();
    }

    async Task SelectVideosAsync(IReadOnlyList<string> paths)
    {
        if (IsBusy || paths.Count == 0) return;
        int version = ++_probeVersion;
        _probeCts?.Cancel();
        var cts = _probeCts = new CancellationTokenSource();

        _selection = new();
        ResetTrim(null);
        HideMessage();
        ResultPanel.Visibility = Visibility.Collapsed;
        string title = paths.Count == 1 ? Path.GetFileName(paths[0]) : $"{paths.Count}개 파일 선택됨";
        SetSelectedInfo(title, "영상 정보를 분석하는 중…", ok: true);
        UpdateAll();

        if (_tools == null)
        {
            SetSelectedInfo(title,
                "FFmpeg를 찾을 수 없어 영상을 분석하지 못했습니다. 오른쪽 위 상태 표시를 눌러 ffmpeg.exe 위치를 지정해 주세요.", ok: false);
            return;
        }

        var infos = new List<VideoInfo>();
        var failed = new List<string>();
        string? lastError = null;
        for (int i = 0; i < paths.Count; i++)
        {
            if (paths.Count > 1) SetSelectedInfo(title, $"영상 정보를 분석하는 중… ({i + 1}/{paths.Count})", ok: true);
            try
            {
                infos.Add(await ProbeCachedAsync(paths[i], cts.Token));
            }
            catch (Exception ex)
            {
                if (version != _probeVersion) return;
                failed.Add(Path.GetFileName(paths[i]));
                lastError = ex.Message;
            }
            if (version != _probeVersion) return;
        }

        _selection = infos;
        string skipped = failed.Count > 0 ? $"\n읽지 못한 {failed.Count}개 파일은 제외했습니다" : "";
        if (infos.Count == 0)
        {
            SetSelectedInfo(title, paths.Count == 1
                ? "영상 정보를 읽지 못했습니다. " + lastError
                : "선택한 파일을 모두 읽지 못했습니다. 녹화 중이거나 손상된 파일일 수 있습니다.", ok: false);
        }
        else if (infos.Count == 1)
        {
            var info = infos[0];
            ResetTrim(info);
            // 한글은 줄바꿈이 글자 단위로 끊기므로 두 줄로 나눠서 표시
            string gop = info.KeyframeIntervalSec > 0 ? $" · 키프레임 {info.KeyframeIntervalSec:0.#}초 간격" : "";
            SetSelectedInfo(info.FileName,
                $"길이 {Format.Duration(info.DurationSec)} · {info.Width}×{info.Height} · {info.Fps:0.##}fps\n{Format.Size(info.SizeBytes)}{gop}{skipped}",
                ok: true);
            ScheduleFrame(true, immediate: true);
            ScheduleFrame(false, immediate: true);
        }
        else
        {
            SetSelectedInfo($"{infos.Count}개 파일 선택됨",
                $"총 길이 {Format.Duration(infos.Sum(v => v.DurationSec))} · {Format.Size(infos.Sum(v => v.SizeBytes))}{skipped}",
                ok: true);
        }
        UpdateAll();
    }

    async Task<VideoInfo> ProbeCachedAsync(string path, CancellationToken ct)
    {
        string key = CacheKeyOf(new FileInfo(path));
        if (ProbeCache.TryGetValue(key, out var cached)) return cached;

        var info = await _tools!.ProbeAsync(path, ct);
        ProbeCache[key] = info;
        DurationCache[key] = info.DurationSec;
        if (_files.FirstOrDefault(f => f.CacheKey == key) is SourceFile item) item.Duration = info.DurationSec;
        return info;
    }

    internal static string CacheKeyOf(FileInfo file) => $"{file.FullName}|{file.Length}|{file.LastWriteTimeUtc.Ticks}";

    void SetSelectedInfo(string name, string meta, bool ok)
    {
        SelectedInfo.Visibility = Visibility.Visible;
        SelectedInfo.Background = (Brush)FindResource(ok ? "SuccessSoftBrush" : "DangerSoftBrush");
        SelectedInfo.BorderBrush = (Brush)FindResource(ok ? "SuccessLineBrush" : "DangerLineBrush");
        SelectedIconBg.Background = (Brush)FindResource(ok ? "SuccessBrush" : "DangerBrush");
        SelectedIcon.Text = ok ? "" : "";
        SelectedName.Text = name;
        SelectedName.ToolTip = IsBatch ? string.Join("\n", _selection.Select(v => v.FileName)) : name;
        SelectedMeta.Text = meta;
    }

    // ───────────────────────── 배속 / 목표 길이 ─────────────────────────

    void SpeedSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (!_ready || _suppressSlider) return;
        _settings.TargetSeconds = null; // 슬라이더를 직접 움직이면 목표 길이 해제
        _settings.SpeedIndex = (int)Math.Round(SpeedSlider.Value);
        UpdateAll();
    }

    /// <summary>목표 길이를 쓰는 중에 슬라이더를 누르면 배속 직접 선택으로 돌아간다</summary>
    void LeaveTargetMode()
    {
        if (_settings.TargetSeconds == null || !SpeedSlider.IsEnabled) return;
        _settings.TargetSeconds = null;
        SetSliderSilently(Math.Round(SpeedSlider.Value));
        _settings.SpeedIndex = (int)SpeedSlider.Value;
        UpdateAll();
    }

    void SetSliderSilently(double value)
    {
        _suppressSlider = true;
        SpeedSlider.Value = Math.Clamp(value, SpeedSlider.Minimum, SpeedSlider.Maximum);
        _suppressSlider = false;
    }

    void BuildTargetChips()
    {
        foreach (int seconds in TimelapsePlanner.TargetSteps)
        {
            var chip = new ToggleButton
            {
                Content = $"{seconds}초",
                Tag = seconds,
                Style = (Style)FindResource("ChoiceChip"),
            };
            chip.Click += TargetChip_Click;
            TargetPanel.Children.Add(chip);
        }
    }

    void TargetChip_Click(object sender, RoutedEventArgs e)
    {
        int seconds = (int)((ToggleButton)sender).Tag;
        if (_settings.TargetSeconds == seconds)
        {
            // 다시 누르면 해제: 지금 배속과 가장 가까운 단계로
            _settings.TargetSeconds = null;
            SetSliderSilently(Math.Round(SpeedSlider.Value));
            _settings.SpeedIndex = (int)SpeedSlider.Value;
        }
        else
        {
            _settings.TargetSeconds = seconds;
        }
        UpdateAll();
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
                if (!SpeedSlider.IsEnabled) return;
                _settings.TargetSeconds = null;
                SetSliderSilently((int)((TextBlock)s).Tag);
                _settings.SpeedIndex = (int)SpeedSlider.Value;
                UpdateAll();
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
        int current = Target == null ? (int)Math.Round(SpeedSlider.Value) : -1; // 목표 길이 사용 중엔 강조 없음
        var accent = (Brush)FindResource("AccentBrush");
        var muted = (Brush)FindResource("TextMutedBrush");
        foreach (TextBlock label in TickCanvas.Children)
        {
            bool on = (int)label.Tag == current;
            label.Foreground = on ? accent : muted;
            label.FontWeight = on ? FontWeights.Bold : FontWeights.Normal;
        }
    }

    /// <summary>파일별 변환 구간 (여러 파일이면 각자 전체 구간)</summary>
    (double Start, double Length) RangeOf(VideoInfo v) =>
        Single != null ? (_trimStart, _trimEnd - _trimStart) : (0, v.DurationSec);

    double SpeedFor(double sourceSeconds) =>
        Target is int t ? TimelapsePlanner.SpeedForTarget(sourceSeconds, t) : ManualSpeed;

    ConversionPlan PlanFor(VideoInfo v)
    {
        var (start, length) = RangeOf(v);
        return TimelapsePlanner.Plan(v, SpeedFor(length), start, length);
    }

    void UpdateAll()
    {
        if (!_ready) return;
        UpdateSpeedUi();
        UpdateTrimUi();
        UpdateButtons();
    }

    void UpdateSpeedUi()
    {
        if (!_ready) return;
        UpdateTargetChips();
        SpeedSlider.Opacity = Target == null ? 1 : 0.55;

        if (_selection.Count == 0)
        {
            SpeedBadgeText.Text = Target is int t ? $"{t}초에 맞춤" : "x" + ManualSpeed;
            OriginalLabel.Text = "원본 길이";
            OutputLabel.Text = "예상 출력 길이";
            OriginalDurationText.Text = "--";
            OutputDurationText.Text = "--";
            EstimateRow.Visibility = Visibility.Collapsed;
        }
        else if (Single is VideoInfo video)
        {
            var plan = PlanFor(video);
            if (Target != null) SetSliderSilently(Math.Log2(plan.Speed)); // 계산된 배속 위치에 손잡이 표시
            SpeedBadgeText.Text = Format.Speed(plan.Speed);
            OriginalLabel.Text = HasTrim ? "구간 길이" : "원본 길이";
            OutputLabel.Text = "예상 출력 길이";
            OriginalDurationText.Text = Format.Duration(plan.SourceSeconds);
            OutputDurationText.Text = Format.Duration(plan.OutputSeconds);
            EstimateText.Text = "약 " + Format.Duration(plan.EstimatedSeconds);
            ModeText.Text = TimelapsePlanner.ModeLabel(plan.Mode);
            ModeChip.ToolTip = TimelapsePlanner.ModeDescription(plan.Mode, video.KeyframeIntervalSec);
            EstimateRow.Visibility = Visibility.Visible;
        }
        else
        {
            var plans = _selection.Select(PlanFor).ToList();
            SpeedBadgeText.Text = Target is int t ? $"각 {t}초" : "x" + ManualSpeed;
            OriginalLabel.Text = $"원본 길이 ({plans.Count}개 합계)";
            OriginalDurationText.Text = Format.Duration(plans.Sum(p => p.SourceSeconds));
            OutputLabel.Text = Target != null ? "파일당 출력 길이" : $"예상 출력 길이 (합계)";
            OutputDurationText.Text = Format.Duration(Target != null ? plans.Max(p => p.OutputSeconds) : plans.Sum(p => p.OutputSeconds));
            EstimateText.Text = "약 " + Format.Duration(plans.Sum(p => p.EstimatedSeconds));
            var modes = plans.Select(p => p.Mode).Distinct().ToList();
            ModeText.Text = modes.Count == 1 ? TimelapsePlanner.ModeLabel(modes[0]) : "파일별 자동 선택";
            ModeChip.ToolTip = string.Join("\n", _selection.Zip(plans, (v, p) =>
                $"{v.FileName} · {Format.Speed(p.Speed)} · {TimelapsePlanner.ModeLabel(p.Mode)}"));
            EstimateRow.Visibility = Visibility.Visible;
        }
        HighlightTick();
        UpdateOutputUi();
    }

    void UpdateTargetChips()
    {
        TargetHint.Text = Target == null ? "배속 자동 계산" : "다시 누르면 해제";
        TargetHint.ToolTip = Target == null
            ? "원하는 결과 길이를 고르면 영상 길이에 맞춰 배속을 자동으로 정합니다"
            : "슬라이더를 움직여도 배속 직접 선택으로 돌아갑니다";
        foreach (ToggleButton chip in TargetPanel.Children)
        {
            int t = (int)chip.Tag;
            chip.IsChecked = Target == t;
            if (Single is VideoInfo)
            {
                double length = _trimEnd - _trimStart;
                chip.ToolTip = length <= t
                    ? $"구간이 {t}초보다 짧아서 x1로 변환됩니다"
                    : $"{Format.Speed(length / t)}로 {t}초 길이 영상을 만듭니다";
            }
            else
            {
                chip.ToolTip = IsBatch
                    ? $"각 파일을 {t}초 길이로 맞춥니다"
                    : $"영상 길이에 맞춰 {t}초가 되도록 배속을 자동으로 정합니다";
            }
        }
    }

    // ───────────────────────── 변환 구간 (앞뒤 자르기) ─────────────────────────

    void ResetTrim(VideoInfo? video)
    {
        _trimStart = 0;
        _trimEnd = video?.DurationSec ?? 0;
        _startFrameCts?.Cancel();
        _endFrameCts?.Cancel();
        SetFrame(StartFrame, null);
        SetFrame(EndFrame, null);
        if (video != null)
        {
            TrimBar.MinimumGap = Math.Min(video.DurationSec, Math.Max(1, video.DurationSec * 0.01));
            TrimBar.SetRange(video.DurationSec, 0, video.DurationSec);
        }
        else
        {
            TrimBar.SetRange(1, 0, 1);
        }
    }

    void ResetTrim_Click(object sender, RoutedEventArgs e)
    {
        if (Single is not VideoInfo video) return;
        ResetTrim(video);
        ScheduleFrame(true, immediate: true);
        ScheduleFrame(false, immediate: true);
        UpdateAll();
    }

    void OnTrimChanging(bool isStart)
    {
        if (Single is not VideoInfo video) return;
        // 화면에 보이는 값과 같도록 초 단위로 맞춤 (끝까지 당기면 영상 끝)
        _trimStart = TrimBar.Start < 0.5 ? 0 : Math.Round(TrimBar.Start);
        _trimEnd = TrimBar.End > video.DurationSec - 0.5 ? video.DurationSec : Math.Round(TrimBar.End);
        ScheduleFrame(isStart);
        UpdateSpeedUi();
        UpdateTrimUi();
    }

    void UpdateTrimUi()
    {
        if (!_ready) return;
        var video = Single;
        TrimEditor.IsEnabled = video != null && !IsBusy;
        TrimEditor.Visibility = IsBatch ? Visibility.Collapsed : Visibility.Visible; // 여러 파일이면 안내 문구만
        if (video == null)
        {
            StartTimeBox.Text = "--";
            EndTimeBox.Text = "--";
            TrimLengthText.Text = "";
            ResetTrimButton.Visibility = Visibility.Hidden;
            TrimSummaryText.Text = IsBatch ? "여러 파일을 고르면 각 파일을 처음부터 끝까지 변환합니다" : TrimHint;
            return;
        }

        bool hours = video.DurationSec >= 3600;
        if (!StartTimeBox.IsKeyboardFocused) StartTimeBox.Text = Format.Timecode(_trimStart, hours);
        if (!EndTimeBox.IsKeyboardFocused) EndTimeBox.Text = Format.Timecode(_trimEnd, hours);
        TrimLengthText.Text = Format.Duration(_trimEnd - _trimStart);
        ResetTrimButton.Visibility = HasTrim && !IsBusy ? Visibility.Visible : Visibility.Hidden;

        if (!HasTrim)
        {
            TrimSummaryText.Text = TrimHint;
            return;
        }
        var cut = new List<string>(2);
        if (_trimStart > 0.5) cut.Add("앞 " + Format.Duration(_trimStart));
        if (_trimEnd < video.DurationSec - 0.5) cut.Add("뒤 " + Format.Duration(video.DurationSec - _trimEnd));
        TrimSummaryText.Text = string.Join(" · ", cut) + " 잘라냄";
    }

    void TimeBox_KeyDown(object sender, KeyEventArgs e)
    {
        var box = (TextBox)sender;
        if (e.Key == Key.Enter)
        {
            ApplyTimeBox(box);
            box.SelectAll();
            e.Handled = true;
        }
        else if (e.Key == Key.Escape)
        {
            Keyboard.ClearFocus();
            UpdateTrimUi();
            e.Handled = true;
        }
    }

    void TimeBox_LostFocus(object sender, KeyboardFocusChangedEventArgs e) => ApplyTimeBox((TextBox)sender);

    void ApplyTimeBox(TextBox box)
    {
        if (Single is not VideoInfo video) return;
        bool isStart = box == StartTimeBox;
        double gap = TrimBar.MinimumGap;
        if (Format.TryParseTimecode(box.Text, out double t))
        {
            if (isStart) _trimStart = Math.Clamp(t, 0, Math.Max(0, _trimEnd - gap));
            else _trimEnd = Math.Clamp(t, Math.Min(_trimStart + gap, video.DurationSec), video.DurationSec);
            TrimBar.SetRange(video.DurationSec, _trimStart, _trimEnd);
            ScheduleFrame(isStart, immediate: true);
            UpdateSpeedUi();
            UpdateButtons();
        }
        // 잘못 입력했으면 원래 값으로, 맞게 입력했으면 정리된 형식으로 다시 표시
        box.Text = Format.Timecode(isStart ? _trimStart : _trimEnd, video.DurationSec >= 3600);
        UpdateTrimUi();
    }

    /// <summary>구간 미리보기 이미지 갱신 예약 (드래그 중에는 잠깐 멈췄을 때만)</summary>
    void ScheduleFrame(bool isStart, bool immediate = false)
    {
        if (isStart) _startFrameDirty = true;
        else _endFrameDirty = true;
        _frameTimer.Stop();
        if (immediate) RefreshFrames();
        else _frameTimer.Start();
    }

    void RefreshFrames()
    {
        if (Single is not VideoInfo video || _tools == null) return;
        if (_startFrameDirty)
        {
            _startFrameDirty = false;
            _startFrameCts?.Cancel();
            _startFrameCts = new CancellationTokenSource();
            _ = LoadFrameAsync(video, _trimStart, StartFrame, _startFrameCts.Token);
        }
        if (_endFrameDirty)
        {
            _endFrameDirty = false;
            _endFrameCts?.Cancel();
            _endFrameCts = new CancellationTokenSource();
            // 영상의 마지막 시각에는 프레임이 없으므로 살짝 앞을 보여준다
            _ = LoadFrameAsync(video, Math.Max(0, _trimEnd - 0.5), EndFrame, _endFrameCts.Token);
        }
    }

    async Task LoadFrameAsync(VideoInfo video, double seconds, Border frame, CancellationToken ct)
    {
        string file = Path.Combine(AppPaths.TempFolder, $"frame_{Guid.NewGuid():N}.jpg");
        try
        {
            bool ok = await _tools!.ExtractFrameAsync(video.Path, seconds, file, ct);
            if (ct.IsCancellationRequested || Single != video) return;
            SetFrame(frame, ok ? LoadBitmap(file) : null);
        }
        catch { /* 미리보기 이미지는 없어도 동작에 지장 없음 */ }
        finally
        {
            try { File.Delete(file); } catch { }
        }
    }

    static BitmapImage LoadBitmap(string file)
    {
        var image = new BitmapImage();
        image.BeginInit();
        image.CacheOption = BitmapCacheOption.OnLoad; // 파일을 바로 지울 수 있도록 메모리로 읽음
        image.UriSource = new Uri(file);
        image.EndInit();
        image.Freeze();
        return image;
    }

    void SetFrame(Border frame, ImageSource? image) =>
        frame.Background = image == null
            ? (Brush)FindResource("TrackBrush")
            : new ImageBrush(image) { Stretch = Stretch.UniformToFill };

    // ───────────────────────── 저장 위치 ─────────────────────────

    void UpdateOutputUi()
    {
        OutputFolderText.Text = _settings.OutputFolder;
        OutputFolderText.ToolTip = _settings.OutputFolder;
        OutputNameText.Text = _selection.Count switch
        {
            0 => "영상을 선택하면 표시됩니다",
            1 => OutputFileName(_selection[0].Path),
            _ => $"{OutputFileName(_selection[0].Path)} 외 {_selection.Count - 1}개",
        };
        OutputNameText.ToolTip = IsBatch ? string.Join("\n", _selection.Select(v => OutputFileName(v.Path))) : OutputNameText.Text;
    }

    // 웹 버전과 같은 규칙: 원본파일명_배속x.mp4 (목표 길이를 쓰면 원본파일명_15s.mp4)
    string OutputFileName(string input)
    {
        string name = Path.GetFileNameWithoutExtension(input);
        return Target is int t ? $"{name}_{t}s.mp4" : $"{name}_{ManualSpeed}x.mp4";
    }

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

    sealed record Job(VideoInfo Video, ConversionPlan Plan, string Target);

    sealed record JobOutcome(Job Job, bool Ok, bool UsedGpu, string? Error, string? Detail);

    sealed record BatchResult(List<JobOutcome> Outcomes, bool Cancelled, TimeSpan Elapsed)
    {
        public List<JobOutcome> Succeeded => Outcomes.Where(o => o.Ok).ToList();
        public List<JobOutcome> Failed => Outcomes.Where(o => !o.Ok).ToList();
    }

    async void Preview_Click(object sender, RoutedEventArgs e)
    {
        if (Single is not VideoInfo video || _tools == null || IsBusy) return;

        var full = PlanFor(video);
        double sample = TimelapsePlanner.PreviewSourceSeconds(video, full.Speed, full.SourceSeconds);
        var plan = TimelapsePlanner.Plan(video, full.Speed, full.StartSeconds, sample);
        string target = Path.Combine(AppPaths.TempFolder, $"preview_{DateTime.Now:HHmmssfff}.mp4");

        _previewWindow?.Close();
        var result = await RunJobsAsync("미리보기 생성", [new Job(video, plan, target)], preview: true);
        if (result.Cancelled)
        {
            ShowMessage("미리보기 생성 작업을 취소했습니다.", isError: false);
            return;
        }
        if (result.Failed.FirstOrDefault() is JobOutcome failed)
        {
            ShowError(failed);
            return;
        }

        string scope = HasTrim ? "선택 구간" : "원본";
        string range = sample >= full.SourceSeconds - 0.01 ? $"{scope} 전체" : $"{scope} 앞 {Format.Duration(sample)}";
        _previewWindow = new PreviewWindow(target,
            $"{Format.Speed(plan.Speed)} · {range} → {Format.Duration(plan.OutputSeconds)} · {TimelapsePlanner.ModeLabel(plan.Mode)}")
        {
            Owner = this,
        };
        _previewWindow.Closed += (_, _) => _previewWindow = null;
        _previewWindow.Show();
    }

    async void Convert_Click(object sender, RoutedEventArgs e)
    {
        if (_selection.Count == 0 || _tools == null || IsBusy) return;

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

        var jobs = new List<Job>();
        var reserved = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var video in _selection)
        {
            string target = Path.GetFullPath(Path.Combine(folder, OutputFileName(video.Path)));
            if (SamePath(target, video.Path))
            {
                ShowMessage("원본 파일과 같은 경로에는 저장할 수 없습니다. 저장 위치를 바꿔 주세요.", isError: true);
                return;
            }
            // 확장자만 다른 원본처럼 결과 이름이 서로 겹치면 뒤 파일 이름을 바꿈
            if (reserved.Contains(target)) target = UniquePath(target, reserved);
            reserved.Add(target);
            jobs.Add(new Job(video, PlanFor(video), target));
        }

        var existing = jobs.Where(j => File.Exists(j.Target)).ToList();
        if (existing.Count > 0)
        {
            string names = string.Join("\n", existing.Take(3).Select(j => Path.GetFileName(j.Target)))
                         + (existing.Count > 3 ? $"\n… 외 {existing.Count - 3}개" : "");
            string head = existing.Count == 1 ? "같은 이름의 파일이 이미 있습니다." : $"같은 이름의 파일이 {existing.Count}개 있습니다.";
            var answer = MessageBox.Show(this,
                $"{head}\n\n{names}\n\n[예] 덮어쓰기\n[아니요] 새 이름으로 저장\n[취소] 그만두기",
                "파일이 이미 있음", MessageBoxButton.YesNoCancel, MessageBoxImage.Question);
            if (answer == MessageBoxResult.Cancel) return;
            if (answer == MessageBoxResult.No)
            {
                for (int i = 0; i < jobs.Count; i++)
                {
                    if (!File.Exists(jobs[i].Target)) continue;
                    string renamed = UniquePath(jobs[i].Target, reserved);
                    reserved.Add(renamed);
                    jobs[i] = jobs[i] with { Target = renamed };
                }
            }
        }

        var result = await RunJobsAsync("변환", jobs, preview: false);
        ShowConvertResult(result, jobs.Count);
        if (result.Succeeded.Count > 0 && SamePath(folder, _settings.SourceFolder)) RefreshFileList();
    }

    void ShowConvertResult(BatchResult result, int jobCount)
    {
        var ok = result.Succeeded;
        var failed = result.Failed;
        long totalSize = ok.Sum(o => File.Exists(o.Job.Target) ? new FileInfo(o.Job.Target).Length : 0);
        bool usedCpu = ok.Any(o => !o.UsedGpu);

        if (ok.Count > 0)
        {
            _lastOutputPath = ok[^1].Job.Target;
            if (jobCount == 1)
            {
                var job = ok[0].Job;
                ResultTitle.Text = usedCpu ? "변환 완료 (CPU)" : "변환 완료";
                ResultPath.Text = job.Target;
                ResultMeta.Text = $"{Format.Speed(job.Plan.Speed)} · {Format.Duration(job.Plan.OutputSeconds)} · "
                                + $"{Format.Size(totalSize)} · 걸린 시간 {Format.Clock(result.Elapsed)}";
                PlayButton.Visibility = Visibility.Visible;
            }
            else
            {
                ResultTitle.Text = $"{ok.Count}개 변환 완료" + (usedCpu ? " (CPU)" : "");
                ResultPath.Text = Path.GetDirectoryName(_lastOutputPath);
                ResultMeta.Text = $"총 {Format.Size(totalSize)} · 걸린 시간 {Format.Clock(result.Elapsed)}";
                PlayButton.Visibility = Visibility.Collapsed;
            }
            ResultPath.ToolTip = string.Join("\n", ok.Select(o => o.Job.Target));
            ResultPanel.Visibility = Visibility.Visible;
        }

        if (result.Cancelled)
            ShowMessage(ok.Count > 0
                ? $"작업을 취소했습니다. 먼저 끝난 {ok.Count}개 파일은 저장되었습니다."
                : "변환 작업을 취소했습니다.", isError: false);
        else if (failed.Count == 1 && jobCount == 1)
            ShowError(failed[0]);
        else if (failed.Count > 0)
            ShowMessage($"{failed.Count}개 파일을 변환하지 못했습니다.\n"
                      + string.Join("\n", failed.Select(f => $"· {f.Job.Video.FileName}: {f.Error}")),
                isError: true, detail: string.Join("\n\n", failed.Select(f => $"{f.Job.Video.FileName}\n{f.Detail}")));

        BringIntoView(ActionCard);
        if (result.Cancelled) return;

        // 창을 내려놓은 사이 끝났으면 Windows 알림, 보고 있으면 소리만
        string title, text;
        if (ok.Count == 0)
        {
            title = "변환 실패";
            text = failed.Count == 1 ? $"{failed[0].Job.Video.FileName}\n{failed[0].Error}" : $"{failed.Count}개 파일을 변환하지 못했습니다.";
        }
        else if (jobCount == 1)
        {
            title = "변환 완료";
            text = $"{Path.GetFileName(ok[0].Job.Target)}\n{Format.Duration(ok[0].Job.Plan.OutputSeconds)} · {Format.Size(totalSize)}";
        }
        else
        {
            title = failed.Count > 0 ? $"{ok.Count}개 변환 완료 · {failed.Count}개 실패" : $"{ok.Count}개 변환 완료";
            text = $"총 {Format.Size(totalSize)} · 걸린 시간 {Format.Clock(result.Elapsed)}";
        }
        bool notified = DevSnapshot.Folder == null && _notifier.NotifyIfInactive(title, text);
        if (!notified && ok.Count > 0) SystemSounds.Asterisk.Play();
    }

    void ShowError(JobOutcome failed) =>
        ShowMessage($"{failed.Error}\n{LastLines(failed.Detail ?? "", 4)}", isError: true, detail: failed.Detail);

    /// <summary>미리보기·변환 공통 실행: 파일을 차례로 처리하며 전체 진행률 표시, 취소, 오류 수집</summary>
    async Task<BatchResult> RunJobsAsync(string verb, IReadOnlyList<Job> jobs, bool preview)
    {
        _cts = new CancellationTokenSource();
        var token = _cts.Token;
        var total = Stopwatch.StartNew();
        var segment = Stopwatch.StartNew(); // GPU 실패 후 CPU 재시도 시 남은 시간 계산을 새로 시작
        double segmentStart = 0;            // segment를 시작할 때의 전체 진행률
        var outcomes = new List<JobOutcome>();
        bool cancelled = false;
        bool many = jobs.Count > 1;

        // 파일마다 예상 작업 시간이 달라서, 진행률은 예상 시간 비율로 합친다
        double totalWeight = Math.Max(jobs.Sum(j => j.Plan.EstimatedSeconds), 0.001);
        double doneWeight = 0;

        HideMessage();
        ResultPanel.Visibility = Visibility.Collapsed;
        ProgressPanel.Visibility = Visibility.Visible;
        ProgressNoteText.Visibility = Visibility.Collapsed;
        ProgressFileText.Visibility = many ? Visibility.Visible : Visibility.Collapsed;
        Progress.Value = 0;
        ProgressText.Text = $"{verb} 준비 중…";
        ProgressTimeText.Text = $"예상 약 {Format.Duration(totalWeight)}";
        Taskbar.ProgressState = TaskbarItemProgressState.Normal;
        Taskbar.ProgressValue = 0;
        UpdateButtons();
        UpdateTrimUi();
        BringIntoView(ActionCard);

        bool useGpu = _gpuAvailable && _settings.UseGpu;
        try
        {
            for (int i = 0; i < jobs.Count; i++)
            {
                var job = jobs[i];
                double weight = job.Plan.EstimatedSeconds;
                double baseWeight = doneWeight; // 앞 파일의 늦게 도착한 진행률 보고가 섞이지 않도록 고정
                string label = many ? $"{verb} 중 ({i + 1}/{jobs.Count})" : $"{verb} 중";
                ProgressFileText.Text = job.Video.FileName;
                ProgressText.Text = label + "…";

                var progress = new Progress<ConvertProgress>(p =>
                {
                    double overall = Math.Min(99.5, (baseWeight + weight * p.Percent / 100) / totalWeight * 100);
                    if (p.Note != null)
                    {
                        ProgressNoteText.Text = p.Note;
                        ProgressNoteText.Visibility = Visibility.Visible;
                        segment.Restart();
                        segmentStart = overall;
                    }
                    Progress.Value = overall;
                    Taskbar.ProgressValue = overall / 100;
                    ProgressText.Text = $"{label}… {overall:0}%";

                    string time = "경과 " + Format.Clock(total.Elapsed);
                    double gained = overall - segmentStart;
                    if (gained >= 3)
                    {
                        double remain = segment.Elapsed.TotalSeconds * (100 - overall) / gained;
                        time += " · 남은 시간 약 " + Format.Clock(TimeSpan.FromSeconds(remain));
                    }
                    ProgressTimeText.Text = time;
                });

                try
                {
                    bool usedGpu = await _tools!.ConvertAsync(job.Video.Path, job.Target, job.Plan, useGpu, preview, progress, token);
                    if (useGpu && !usedGpu) useGpu = false; // GPU가 안 되는 게 확인되면 남은 파일은 바로 CPU로
                    outcomes.Add(new JobOutcome(job, true, usedGpu, null, null));
                }
                catch (Exception) when (token.IsCancellationRequested)
                {
                    cancelled = true;
                    break;
                }
                catch (FfmpegException ex)
                {
                    outcomes.Add(new JobOutcome(job, false, false, ex.Message, ex.Detail));
                }
                catch (Exception ex)
                {
                    outcomes.Add(new JobOutcome(job, false, false, $"{verb} 중 오류가 발생했습니다: {ex.Message}", null));
                }
                doneWeight += weight;
            }
        }
        finally
        {
            _cts.Dispose();
            _cts = null;
            ProgressPanel.Visibility = Visibility.Collapsed;
            Taskbar.ProgressState = TaskbarItemProgressState.None;
            UpdateButtons();
            UpdateTrimUi();
            if (_closeAfterJob) _ = Dispatcher.InvokeAsync(Close);
        }
        return new BatchResult(outcomes, cancelled, total.Elapsed);
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

    static string[] DroppedVideos(DragEventArgs e) =>
        e.Data.GetData(DataFormats.FileDrop) is string[] files ? files.Where(IsVideoFile).ToArray() : [];

    void Window_DragEnter(object sender, DragEventArgs e)
    {
        int count = IsBusy ? 0 : DroppedVideos(e).Length;
        e.Effects = count > 0 ? DragDropEffects.Copy : DragDropEffects.None;
        DropText.Text = count > 1 ? $"여기에 놓으면 영상 {count}개를 선택합니다" : "여기에 놓으면 이 영상을 선택합니다";
        DropOverlay.Visibility = count > 0 ? Visibility.Visible : Visibility.Collapsed;
        e.Handled = true;
    }

    void Window_DragOver(object sender, DragEventArgs e)
    {
        e.Effects = !IsBusy && DroppedVideos(e).Length > 0 ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    void Window_DragLeave(object sender, DragEventArgs e) => DropOverlay.Visibility = Visibility.Collapsed;

    async void Window_Drop(object sender, DragEventArgs e)
    {
        DropOverlay.Visibility = Visibility.Collapsed;
        var paths = DroppedVideos(e);
        if (paths.Length == 0 || IsBusy) return;
        Activate();
        await SelectExternalAsync(paths);
    }

    // ───────────────────────── 공통 UI ─────────────────────────

    void UpdateButtons()
    {
        if (!_ready) return;
        bool busy = IsBusy;
        bool ready = _selection.Count > 0 && _tools != null && !busy;

        PreviewButton.IsEnabled = ready && !IsBatch;
        PreviewButton.ToolTip = IsBatch
            ? "미리보기는 파일을 하나만 골랐을 때 쓸 수 있습니다"
            : HasTrim
                ? "선택한 구간의 앞부분을 실제 배속으로 짧게 변환해서 보여줍니다"
                : "앞부분을 실제 배속으로 짧게 변환해서 보여줍니다";
        ConvertButton.IsEnabled = ready;
        ConvertText.Text = IsBatch ? $"{_selection.Count}개 변환 시작" : "변환 시작";
        PreviewButton.Visibility = busy ? Visibility.Collapsed : Visibility.Visible;
        ConvertButton.Visibility = busy ? Visibility.Collapsed : Visibility.Visible;
        CancelButton.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;

        FileList.IsEnabled = !busy;
        OpenFileButton.IsEnabled = !busy;
        ChangeSourceButton.IsEnabled = !busy;
        RefreshButton.IsEnabled = !busy;
        ChangeOutputButton.IsEnabled = !busy;
        SpeedSlider.IsEnabled = !busy;
        TargetPanel.IsEnabled = !busy;
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

    /// <summary>오른쪽 영역이 스크롤될 만큼 길 때 진행률·결과가 카드 여백까지 온전히 보이도록</summary>
    void BringIntoView(FrameworkElement element) =>
        Dispatcher.InvokeAsync(() => element.BringIntoView(), DispatcherPriority.Loaded);

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
                // FFmpeg를 멈추고 임시 파일을 지운 뒤 RunJobsAsync가 창을 닫는다
                _closeAfterJob = true;
                _cts?.Cancel();
            }
            return;
        }

        _probeCts?.Cancel();
        _durationCts?.Cancel();
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

    /// <summary>"이름 (2).mp4" 형식으로 아직 없는(그리고 이번에 쓸 예정이 아닌) 경로</summary>
    static string UniquePath(string path, ISet<string> reserved)
    {
        string dir = Path.GetDirectoryName(path)!;
        string name = Path.GetFileNameWithoutExtension(path);
        string ext = Path.GetExtension(path);
        for (int i = 2; ; i++)
        {
            string candidate = Path.Combine(dir, $"{name} ({i}){ext}");
            if (!File.Exists(candidate) && !reserved.Contains(candidate)) return candidate;
        }
    }

    static string LastLines(string text, int count) =>
        string.Join("\n", text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).TakeLast(count));
}
