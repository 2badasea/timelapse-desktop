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
///   --convert &lt;배속단계 0~12&gt; &lt;저장폴더&gt;   변환·미리보기까지 실행하고 진행/완료/미리보기 화면 저장
/// </summary>
static class DevSnapshot
{
    const double Scale = 2; // 문서 인쇄용으로 2배 해상도

    public static string? Folder { get; private set; }
    public static string? Source { get; private set; }
    public static string? File { get; private set; }
    public static int? ConvertSpeedIndex { get; private set; }
    public static string? ConvertOutput { get; private set; }

    public static void Parse(string[] args)
    {
        for (int i = 0; i < args.Length - 1; i++)
        {
            if (args[i] == "--snapshot") Folder = args[i + 1];
            if (args[i] == "--source") Source = args[i + 1];
            if (args[i] == "--file") File = args[i + 1];
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
