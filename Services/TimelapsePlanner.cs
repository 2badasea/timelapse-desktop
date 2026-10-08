namespace TimelapseConverter.Services;

public sealed record VideoInfo(
    string Path,
    double DurationSec,
    int Width,
    int Height,
    double Fps,
    double KeyframeIntervalSec,   // 0 = 알 수 없음
    long SizeBytes)
{
    public string FileName => System.IO.Path.GetFileName(Path);
}

public enum ConversionMode
{
    /// <summary>모든 프레임 디코딩 후 N번째 프레임 선택 (가장 정확, 가장 느림)</summary>
    Full,
    /// <summary>참조 프레임(I/P)만 디코딩, B-프레임 건너뜀</summary>
    Reference,
    /// <summary>키프레임(I-frame)만 읽음 (가장 빠름)</summary>
    Keyframe,
}

public sealed record ConversionPlan(
    int Speed,
    ConversionMode Mode,
    double SourceSeconds,      // 실제로 처리할 원본 구간 길이
    double? LimitSeconds,      // 앞부분만 처리할 때(미리보기) 구간 길이
    double OutputSeconds,
    double EstimatedSeconds);

/// <summary>
/// 배속별 FFmpeg 처리 방식 결정 및 예상 시간 계산.
/// 웹 버전은 x128 기준으로 두 가지 모드만 썼지만, 여기서는 영상의 키프레임 간격을 보고
/// 출력 프레임이 비지 않는 범위에서 가장 빠른 모드를 고른다.
/// </summary>
public static class TimelapsePlanner
{
    public static readonly int[] SpeedSteps = { 1, 2, 4, 8, 16, 32, 64, 128, 256, 512, 1024, 2048, 4096 };

    // 고속/키프레임 모드의 출력 프레임레이트 (웹 버전의 -r 30과 동일)
    public const int OutputFps = 30;

    const int ReferenceModeMinSpeed = 16;
    const int LegacyKeyframeSpeed = 128;

    public static ConversionMode ChooseMode(int speed, double keyframeInterval)
    {
        if (speed < ReferenceModeMinSpeed) return ConversionMode.Full;

        // 출력 1프레임이 원본에서 차지하는 시간(speed/30초)이 키프레임 간격 이상이면
        // 키프레임만 읽어도 모든 출력 프레임이 새 장면으로 채워진다.
        bool keyframeEnough = keyframeInterval > 0
            ? (double)speed / OutputFps >= keyframeInterval
            : speed >= LegacyKeyframeSpeed;
        return keyframeEnough ? ConversionMode.Keyframe : ConversionMode.Reference;
    }

    /// <summary>키프레임 모드가 적용되기 시작하는 최소 배속 (없으면 null)</summary>
    public static int? KeyframeThreshold(double keyframeInterval) =>
        SpeedSteps.Where(s => ChooseMode(s, keyframeInterval) == ConversionMode.Keyframe)
                  .Select(s => (int?)s).FirstOrDefault();

    public static ConversionPlan Plan(VideoInfo video, int speed, double? limitSeconds = null)
    {
        var mode = ChooseMode(speed, video.KeyframeIntervalSec);
        double? limit = limitSeconds is double l && l < video.DurationSec ? l : null;
        double source = limit ?? video.DurationSec;

        // 실측 기준(GTX 1650, 1080p30): 전체 ≈ 25배속, 참조 ≈ 70배속, 키프레임 ≈ 초당 170개 처리
        double gop = video.KeyframeIntervalSec > 0 ? video.KeyframeIntervalSec : 2;
        double estimate = mode switch
        {
            ConversionMode.Keyframe => source / gop / 170.0,
            ConversionMode.Reference => source / 70.0,
            _ => source / 25.0,
        };

        return new ConversionPlan(speed, mode, source, limit, source / speed, Math.Max(1, estimate + 1));
    }

    /// <summary>
    /// 미리보기용 원본 구간 길이. 결과가 약 8초가 되도록 잡되, 모드별 처리 시간이 너무 길어지지 않게 상한을 둔다.
    /// </summary>
    public static double PreviewSourceSeconds(VideoInfo video, int speed)
    {
        double cap = ChooseMode(speed, video.KeyframeIntervalSec) switch
        {
            ConversionMode.Keyframe => 3600,
            ConversionMode.Reference => 1200,
            _ => 300,
        };
        return Math.Min(video.DurationSec, Math.Min(speed * 8.0, cap));
    }

    public static string ModeLabel(ConversionMode mode) => mode switch
    {
        ConversionMode.Keyframe => "키프레임 고속 모드",
        ConversionMode.Reference => "고속 모드",
        _ => "정밀 모드",
    };

    public static string ModeDescription(ConversionMode mode, double keyframeInterval)
    {
        switch (mode)
        {
            case ConversionMode.Keyframe:
                string gop = keyframeInterval > 0 ? $"이 영상은 키프레임이 {keyframeInterval:0.#}초 간격이라 " : "";
                return "키프레임(I-frame)만 읽어서 가장 빠르게 처리합니다.\n"
                     + $"{gop}x{KeyframeThreshold(keyframeInterval)} 이상에서 자동 적용됩니다.";
            case ConversionMode.Reference:
                return "B-프레임을 건너뛰고 참조 프레임만 디코딩해 빠르게 처리합니다.\n"
                     + $"x{ReferenceModeMinSpeed} 이상에서 자동 적용됩니다.";
            default:
                return "모든 프레임을 디코딩한 뒤 N번째 프레임만 골라 가장 정확하게 처리합니다.\n"
                     + $"x{ReferenceModeMinSpeed} 미만에서 사용됩니다.";
        }
    }
}
