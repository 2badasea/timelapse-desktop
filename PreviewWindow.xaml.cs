using System.Diagnostics;
using System.Windows;
using TimelapseConverter.Helpers;

namespace TimelapseConverter;

/// <summary>
/// 앞부분을 실제 배속으로 변환한 샘플을 반복 재생한다.
/// (웹 버전은 브라우저 재생속도로 흉내 내서 x16까지만 볼 수 있었음)
/// </summary>
public partial class PreviewWindow : Window
{
    readonly string _path;
    bool _playing;

    public PreviewWindow(string path, string caption)
    {
        InitializeComponent();
        _path = path;
        CaptionText.Text = caption;
        CaptionText.ToolTip = caption;
        Loaded += (_, _) =>
        {
            Player.Source = new Uri(path);
            Play();
        };
        Closed += (_, _) => Player.Close();
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        TitleBar.Apply(this);
    }

    void Play()
    {
        Player.Play();
        _playing = true;
        PlayPauseIcon.Text = "";
        PlayPauseText.Text = "일시정지";
    }

    void Pause()
    {
        Player.Pause();
        _playing = false;
        PlayPauseIcon.Text = "";
        PlayPauseText.Text = "재생";
    }

    void Player_MediaOpened(object sender, RoutedEventArgs e) => PlayerMessage.Visibility = Visibility.Collapsed;

    void Player_MediaEnded(object sender, RoutedEventArgs e)
    {
        Player.Position = TimeSpan.Zero;
        Player.Play();
    }

    void Player_MediaFailed(object? sender, ExceptionRoutedEventArgs e)
    {
        PlayerMessage.Text = "이 창에서 영상을 재생하지 못했습니다.\n[기본 플레이어로 열기]를 눌러 확인해 주세요.\n\n" + e.ErrorException?.Message;
        PlayerMessage.Visibility = Visibility.Visible;
    }

    void PlayPause_Click(object sender, RoutedEventArgs e)
    {
        if (_playing) Pause(); else Play();
    }

    void OpenExternal_Click(object sender, RoutedEventArgs e)
    {
        Pause();
        try { Process.Start(new ProcessStartInfo(_path) { UseShellExecute = true }); }
        catch (Exception ex) { MessageBox.Show(this, "영상을 열 수 없습니다: " + ex.Message, "미리보기"); }
    }

    void Close_Click(object sender, RoutedEventArgs e) => Close();
}
