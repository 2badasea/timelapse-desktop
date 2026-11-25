using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace TimelapseConverter.Helpers;

/// <summary>
/// 개발용 화면 캡처 (가이드 문서 이미지 제작용). 일반 실행에는 영향 없음.
///   --snapshot &lt;폴더&gt;                    캡처 저장 폴더 (지정하면 캡처 후 자동 종료)
///   --source &lt;폴더&gt;                      녹화 폴더를 이번 실행에서만 바꿈 (설정에 저장 안 함)
///   --file &lt;영상&gt;                        선택할 영상
///   --target &lt;초&gt;                        목표 길이 선택 (10/15/20/30/60)
///   --trim &lt;시작초&gt; &lt;끝초&gt;               변환 구간 지정
///   --convert &lt;배속단계 0~12&gt; &lt;저장폴더&gt;   변환·미리보기까지 실행하고 진행/완료/미리보기 화면 저장
///   --batch                             녹화 폴더 목록을 모두 선택한 화면 저장 (--convert가 있으면 한꺼번에 변환까지)
///   --notify-test                       완료 알림을 한 번 띄워 보고 결과를 notify.txt에 저장
///   --selftest                          (--file과 함께) 목표 길이·구간 입력 동작을 확인하고 selftest.txt에 저장 후 종료
/// </summary>
static class DevSnapshot
{
    const double Scale = 2; // 문서 인쇄용으로 2배 해상도

    public static string? Folder { get; private set; }
    public static string? Source { get; private set; }
    public static string? File { get; private set; }
    public static int? Target { get; private set; }
    public static (double Start, double End)? Trim { get; private set; }
    public static int? ConvertSpeedIndex { get; private set; }
    public static string? ConvertOutput { get; private set; }
    public static bool Batch { get; private set; }
    public static bool NotifyTest { get; private set; }
    public static bool SelfTest { get; private set; }

    public static void Parse(string[] args)
    {
        var inv = CultureInfo.InvariantCulture;
        for (int i = 0; i < args.Length; i++)
        {
            bool hasNext = i + 1 < args.Length;
            if (args[i] == "--batch") Batch = true;
            if (args[i] == "--notify-test") NotifyTest = true;
            if (args[i] == "--selftest") SelfTest = true;
            if (!hasNext) continue;
            if (args[i] == "--snapshot") Folder = args[i + 1];
            if (args[i] == "--source") Source = args[i + 1];
            if (args[i] == "--file") File = args[i + 1];
            if (args[i] == "--target" && int.TryParse(args[i + 1], out int target)) Target = target;
            if (args[i] == "--trim" && i + 2 < args.Length
                && double.TryParse(args[i + 1], NumberStyles.Float, inv, out double start)
                && double.TryParse(args[i + 2], NumberStyles.Float, inv, out double end))
                Trim = (start, end);
            if (args[i] == "--convert" && i + 2 < args.Length && int.TryParse(args[i + 1], out int speed))
            {
                ConvertSpeedIndex = speed;
                ConvertOutput = args[i + 2];
            }
        }
    }

    public static void Save(Window window, string name)
    {
        if (Folder == null || VisualTreeHelper.GetChildrenCount(window) == 0) return;
        var root = (FrameworkElement)VisualTreeHelper.GetChild(window, 0);
        root.UpdateLayout();
        var bitmap = new RenderTargetBitmap(
            (int)Math.Ceiling(root.ActualWidth * Scale), (int)Math.Ceiling(root.ActualHeight * Scale),
            96 * Scale, 96 * Scale, PixelFormats.Pbgra32);
        bitmap.Render(root);

        Directory.CreateDirectory(Folder);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = System.IO.File.Create(Path.Combine(Folder, name + ".png"));
        encoder.Save(stream);
    }
}
