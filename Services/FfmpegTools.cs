using System.Globalization;
using System.IO;
using System.Text.Json;

namespace TimelapseConverter.Services;

public sealed class FfmpegException(string message, string detail) : Exception(message)
{
    public string Detail { get; } = detail;
}

/// <param name="Percent">0~100</param>
/// <param name="Note">진행 중 안내 문구 (예: GPU 실패 후 CPU 재시도)</param>
public sealed record ConvertProgress(double Percent, string? Note = null);

/// <summary>
/// ffmpeg.exe / ffprobe.exe 위치 탐색, 영상 분석, 타임랩스 변환
/// </summary>
public sealed class FfmpegTools
{
    static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    public string FfmpegPath { get; }
    public string FfprobePath { get; }
    public string Folder => Path.GetDirectoryName(FfmpegPath)!;

    FfmpegTools(string ffmpeg, string ffprobe)
    {
        FfmpegPath = ffmpeg;
        FfprobePath = ffprobe;
    }

    // ───────────────────────── 위치 탐색 ─────────────────────────

    /// <summary>
    /// 지정 폴더 → exe 옆 → exe 옆 ffmpeg\bin → PATH → C:\ffmpeg\bin 순서로 찾는다.
    /// </summary>
    public static FfmpegTools? Locate(string? preferredFolder)
    {
        foreach (var dir in CandidateFolders(preferredFolder))
        {
            try
            {
                string ffmpeg = Path.Combine(dir, "ffmpeg.exe");
                string ffprobe = Path.Combine(dir, "ffprobe.exe");
                if (File.Exists(ffmpeg) && File.Exists(ffprobe)) return new FfmpegTools(ffmpeg, ffprobe);
            }
            catch { /* 잘못된 PATH 항목 무시 */ }
        }
        return null;
    }

    static IEnumerable<string> CandidateFolders(string? preferred)
    {
        if (!string.IsNullOrWhiteSpace(preferred)) yield return preferred;

        string baseDir = AppContext.BaseDirectory;
        yield return baseDir;
        yield return Path.Combine(baseDir, "ffmpeg");
        yield return Path.Combine(baseDir, "ffmpeg", "bin");

        string path = Environment.GetEnvironmentVariable("PATH") ?? "";
        foreach (var p in path.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            yield return p.Trim('"');

        yield return @"C:\ffmpeg\bin";
    }

    /// <summary>NVIDIA 하드웨어 인코더(h264_nvenc)를 실제로 쓸 수 있는지 짧게 시험한다.</summary>
    public async Task<bool> CheckNvencAsync()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        try
        {
            var r = await ProcessRunner.RunAsync(FfmpegPath,
                ["-hide_banner", "-nostdin", "-loglevel", "error",
                 "-f", "lavfi", "-i", "color=c=black:s=256x256:d=0.2",
                 "-c:v", "h264_nvenc", "-f", "null", "-"], timeout.Token);
            return r.ExitCode == 0;
        }
        catch { return false; }
    }

    // ───────────────────────── 영상 분석 ─────────────────────────

    public async Task<VideoInfo> ProbeAsync(string path, CancellationToken ct)
    {
        var r = await ProcessRunner.RunAsync(FfprobePath,
            ["-v", "error", "-print_format", "json",
             "-show_entries", "format=duration:stream=codec_type,width,height,avg_frame_rate,r_frame_rate,duration",
             path], ct);
        if (r.ExitCode != 0) throw new FfmpegException("영상 정보를 읽을 수 없습니다.", r.ErrorTail);

        using var doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(r.Stdout) ? "{}" : r.Stdout);
        var root = doc.RootElement;

        JsonElement? video = null;
        if (root.TryGetProperty("streams", out var streams))
        {
            foreach (var s in streams.EnumerateArray())
            {
                if (s.TryGetProperty("codec_type", out var t) && t.GetString() == "video") { video = s; break; }
            }
        }
        if (video is not JsonElement v) throw new FfmpegException("영상(비디오) 트랙이 없는 파일입니다.", r.ErrorTail);

        double duration = ReadDouble(root, "format", "duration");
        if (duration <= 0) duration = ReadNumberString(v, "duration");
        if (duration <= 0)
            throw new FfmpegException("영상 길이를 읽을 수 없습니다. 녹화 중이거나 손상된 파일일 수 있습니다.", r.ErrorTail);

        int width = v.TryGetProperty("width", out var w) ? w.GetInt32() : 0;
        int height = v.TryGetProperty("height", out var h) ? h.GetInt32() : 0;
        double fps = ParseRate(v, "avg_frame_rate");
        if (fps <= 0) fps = ParseRate(v, "r_frame_rate");

        double gop = await ProbeKeyframeIntervalAsync(path, duration, ct);
        long size = new FileInfo(path).Length;

        return new VideoInfo(path, duration, width, height, fps, gop, size);
    }

    /// <summary>
    /// 앞 60초 구간의 패킷 플래그만 읽어 키프레임 간격(중앙값)을 구한다. 디코딩하지 않아 빠르다.
    /// </summary>
    async Task<double> ProbeKeyframeIntervalAsync(string path, double duration, CancellationToken ct)
    {
        int window = (int)Math.Ceiling(Math.Clamp(duration, 1, 60));
        var r = await ProcessRunner.RunAsync(FfprobePath,
            ["-v", "error", "-select_streams", "v:0", "-read_intervals", $"%+{window}",
             "-show_entries", "packet=pts_time,flags", "-of", "csv=p=0", path], ct);
        if (r.ExitCode != 0) return 0;

        var keyframes = new List<double>();
        foreach (var line in r.Stdout.Split('\n'))
        {
            var parts = line.Trim().Split(',');
            if (parts.Length >= 2 && parts[1].Contains('K')
                && double.TryParse(parts[0], NumberStyles.Float, Inv, out var t))
                keyframes.Add(t);
        }
        keyframes.Sort();

        // 구간 안에 키프레임이 하나뿐이면 간격이 최소한 구간 길이 이상이다
        if (keyframes.Count < 2) return keyframes.Count == 1 ? window : 0;

        var gaps = keyframes.Zip(keyframes.Skip(1), (a, b) => b - a).Where(d => d > 0.001).OrderBy(d => d).ToList();
        return gaps.Count == 0 ? 0 : gaps[gaps.Count / 2];
    }

    static double ReadDouble(JsonElement root, string obj, string prop) =>
        root.TryGetProperty(obj, out var o) ? ReadNumberString(o, prop) : 0;

    static double ReadNumberString(JsonElement e, string prop)
    {
        if (!e.TryGetProperty(prop, out var p)) return 0;
        if (p.ValueKind == JsonValueKind.Number) return p.GetDouble();
        return double.TryParse(p.GetString(), NumberStyles.Float, Inv, out var d) ? d : 0;
    }

    static double ParseRate(JsonElement e, string prop)
    {
        if (!e.TryGetProperty(prop, out var p)) return 0;
        var parts = (p.GetString() ?? "").Split('/');
        if (parts.Length == 2
            && double.TryParse(parts[0], NumberStyles.Float, Inv, out var num)
            && double.TryParse(parts[1], NumberStyles.Float, Inv, out var den) && den > 0)
            return num / den;
        return 0;
    }

    // ───────────────────────── 변환 ─────────────────────────

    /// <summary>
    /// 타임랩스 변환. 임시 파일(.part)에 쓴 뒤 성공하면 최종 이름으로 바꾼다.
    /// GPU 변환이 실패하면 CPU(libx264)로 자동 재시도한다.
    /// </summary>
    /// <returns>GPU로 변환했으면 true</returns>
    public async Task<bool> ConvertAsync(string input, string output, ConversionPlan plan, bool useGpu, bool preview,
        IProgress<ConvertProgress> progress, CancellationToken ct)
    {
        string part = output + ".part";
        try
        {
            if (useGpu)
            {
                var gpu = await EncodeAsync(input, part, plan, gpu: true, preview, progress, ct);
                ct.ThrowIfCancellationRequested();
                if (gpu.ExitCode == 0 && IsValidOutput(part))
                {
                    File.Move(part, output, overwrite: true);
                    return true;
                }
                progress.Report(new ConvertProgress(0, "GPU 가속 변환에 실패해서 CPU로 다시 시도하는 중입니다…"));
            }

            var cpu = await EncodeAsync(input, part, plan, gpu: false, preview, progress, ct);
            ct.ThrowIfCancellationRequested();
            if (cpu.ExitCode == 0 && IsValidOutput(part))
            {
                File.Move(part, output, overwrite: true);
                return false;
            }
            throw new FfmpegException("FFmpeg 변환에 실패했습니다.", cpu.ErrorTail);
        }
        finally
        {
            try { if (File.Exists(part)) File.Delete(part); } catch { }
        }
    }

    static bool IsValidOutput(string path) => File.Exists(path) && new FileInfo(path).Length > 0;

    Task<ProcessResult> EncodeAsync(string input, string output, ConversionPlan plan, bool gpu, bool preview,
        IProgress<ConvertProgress> progress, CancellationToken ct)
    {
        double expectedOutputSec = Math.Max(plan.OutputSeconds, 0.001);
        var args = BuildEncodeArgs(input, output, plan, gpu, preview);

        return ProcessRunner.RunAsync(FfmpegPath, args, ct, line =>
        {
            // -progress의 out_time은 "출력" 영상 기준 시간(마이크로초)이므로 예상 출력 길이로 나눠야 한다.
            // (웹 버전은 원본 길이로 나눠서 배속이 클수록 진행률이 거의 안 올라가는 문제가 있었음)
            int eq = line.IndexOf('=');
            if (eq <= 0) return;
            string key = line[..eq];
            if (key != "out_time_us" && key != "out_time_ms") return;
            if (!long.TryParse(line[(eq + 1)..].Trim(), out long us) || us < 0) return;

            double percent = Math.Clamp(us / 1_000_000.0 / expectedOutputSec * 100, 0, 99.5);
            progress.Report(new ConvertProgress(percent));
        });
    }

    static List<string> BuildEncodeArgs(string input, string output, ConversionPlan plan, bool gpu, bool preview)
    {
        var a = new List<string> { "-hide_banner", "-nostdin", "-y", "-loglevel", "error" };

        string? skip = plan.Mode switch
        {
            ConversionMode.Keyframe => "nokey",   // 키프레임만
            ConversionMode.Reference => "noref",  // B-프레임 제외
            _ => null,
        };

        if (gpu)
        {
            a.AddRange(["-hwaccel", "cuda"]);
            // 디코딩한 프레임을 GPU 메모리에 둔 채 바로 인코딩 (CPU↔GPU 전송 제거)
            if (skip != null) a.AddRange(["-hwaccel_output_format", "cuda"]);
        }
        if (skip != null)
        {
            // -discard: 디스크에서 건너뛸 패킷을 아예 읽지 않음 / -skip_frame: 디코딩 생략
            a.AddRange(["-skip_frame", skip, "-discard", skip]);
        }
        if (plan.LimitSeconds is double limit)
            a.AddRange(["-t", limit.ToString("0.###", Inv)]);

        a.AddRange(["-i", input]);

        if (skip == null)
            a.AddRange(["-vf", $"select=not(mod(n\\,{plan.Speed})),setpts=N/FRAME_RATE/TB"]);
        else
            a.AddRange(["-vf", $"setpts=PTS/{plan.Speed}", "-r", TimelapsePlanner.OutputFps.ToString(Inv)]);

        a.Add("-an"); // 타임랩스라 오디오 제거

        if (gpu)
            a.AddRange(["-c:v", "h264_nvenc", "-cq", "18", "-preset", preview ? "p1" : "p4"]);
        else
            a.AddRange(["-c:v", "libx264", "-crf", "18", "-preset", preview ? "ultrafast" : "fast", "-pix_fmt", "yuv420p"]);

        a.AddRange(["-movflags", "+faststart", "-progress", "pipe:1", "-nostats", "-f", "mp4", output]);
        return a;
    }
}
