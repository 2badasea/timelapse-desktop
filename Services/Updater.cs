using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using TimelapseConverter.Helpers;

namespace TimelapseConverter.Services;

/// <param name="DownloadUrl">자동 업데이트용 zip 주소 (없으면 릴리스 페이지에서 직접 받아야 함)</param>
/// <param name="Sha256">zip의 SHA-256 (GitHub가 첨부 파일마다 계산해 둔 값)</param>
public sealed record UpdateInfo(
    Version Version, string Notes, string PageUrl,
    string? AssetName, string? DownloadUrl, long Size, string? Sha256)
{
    public bool CanAutoUpdate => DownloadUrl != null && Sha256 != null && AssetName != null;
}

public sealed record DownloadProgress(long Done, long Total);

public sealed class UpdateException(string message) : Exception(message);

/// <summary>
/// 원클릭 업데이트.
/// 1) GitHub의 최신 릴리스 정보(JSON)를 읽어 지금 버전과 비교
/// 2) 릴리스 zip을 내려받으며 SHA-256 계산 → GitHub가 알려 준 값과 다르면 버림
/// 3) 압축을 풀어 바뀐 파일만 교체. 실행 중인 exe는 덮어쓸 수 없지만 이름은 바꿀 수 있으므로
///    지금 exe를 *.tlc-old로 바꾸고 새 exe를 넣는다 (중간에 실패하면 원래대로 되돌림)
/// 4) 새 exe를 실행하고 지금 프로그램은 종료. 남은 *.tlc-old 파일은 다음 실행 때 지운다
/// </summary>
public static class Updater
{
    const string LatestApi = "https://api.github.com/repos/2badasea/timelapse-desktop/releases/latest";
    public const string ReleasesPage = "https://github.com/2badasea/timelapse-desktop/releases/latest";
    public const string ExeName = "TimelapseConverter.exe";

    const string OldSuffix = ".tlc-old";                  // 업데이트가 만든 백업 파일 (다른 파일과 겹치지 않는 이름)
    const string StagingName = "TimelapseConverter.update"; // 내려받기·압축 해제용 임시 폴더
    static readonly Regex AssetPattern = new(@"^TimeLapseConverter-v[\d.]+-win-x64\.zip$", RegexOptions.IgnoreCase);

    // 정적 초기화는 적힌 순서대로 실행되므로 CurrentVersion이 HttpClient(User-Agent)보다 먼저 와야 한다
    public static Version CurrentVersion { get; } = Normalize(typeof(Updater).Assembly.GetName().Version);

    static readonly HttpClient Http = CreateClient();

    /// <summary>업데이트 직후 새 버전으로 실행됐을 때 이전 버전 (--updated-from)</summary>
    public static string? UpdatedFrom { get; private set; }

    public static string AppFolder => Path.GetDirectoryName(Environment.ProcessPath ?? AppContext.BaseDirectory)!;
    static string StagingFolder => Path.Combine(AppFolder, StagingName);

    static HttpClient CreateClient()
    {
        var http = new HttpClient { Timeout = Timeout.InfiniteTimeSpan }; // 큰 파일이라 시간 제한은 요청별로
        http.DefaultRequestHeaders.UserAgent.ParseAdd($"TimeLapseConverter/{CurrentVersion.ToString(3)}");
        return http;
    }

    public static void ParseArgs(string[] args)
    {
        int i = Array.IndexOf(args, "--updated-from");
        if (i >= 0 && i + 1 < args.Length) UpdatedFrom = args[i + 1];
    }

    // ───────────────────────── 1) 새 버전 확인 ─────────────────────────

    /// <returns>새 버전이 없으면 null. 인터넷 연결 문제 등은 예외로 알린다.</returns>
    public static async Task<UpdateInfo?> CheckAsync(CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(15));

        using var request = new HttpRequestMessage(HttpMethod.Get, DevSnapshot.UpdateFeed ?? LatestApi);
        request.Headers.Accept.ParseAdd("application/vnd.github+json");
        using var response = await Http.SendAsync(request, timeout.Token);
        response.EnsureSuccessStatusCode();

        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(timeout.Token));
        var root = doc.RootElement;
        if (Bool(root, "draft") || Bool(root, "prerelease")) return null;

        string tag = Str(root, "tag_name") ?? "";
        if (!TryParseVersion(tag, out var version) || version <= CurrentVersion) return null;

        string? assetName = null, url = null, sha = null;
        long size = 0;
        if (root.TryGetProperty("assets", out var assets) && assets.ValueKind == JsonValueKind.Array)
        {
            foreach (var asset in assets.EnumerateArray())
            {
                string? name = Str(asset, "name");
                if (name == null || !AssetPattern.IsMatch(name)) continue;
                string? digest = Str(asset, "digest");
                string? link = Str(asset, "browser_download_url");
                if (link == null || !IsTrustedDownload(link)) continue;
                assetName = name;
                url = link;
                size = asset.TryGetProperty("size", out var s) && s.TryGetInt64(out long n) ? n : 0;
                sha = digest != null && digest.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase) ? digest[7..] : null;
                break;
            }
        }

        string page = Str(root, "html_url") ?? ReleasesPage;
        return new UpdateInfo(version, CleanNotes(Str(root, "body") ?? ""), page, assetName, url, size, sha);
    }

    /// <summary>실제 배포에서는 GitHub의 https 주소만 받는다 (개발용 로컬 피드는 예외)</summary>
    static bool IsTrustedDownload(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)) return false;
        if (DevSnapshot.UpdateFeed != null) return uri.IsLoopback;
        return uri.Scheme == Uri.UriSchemeHttps && uri.Host.Equals("github.com", StringComparison.OrdinalIgnoreCase);
    }

    public static bool TryParseVersion(string tag, out Version version)
    {
        version = new Version(0, 0, 0);
        if (!Version.TryParse(tag.Trim().TrimStart('v', 'V'), out var parsed)) return false;
        version = Normalize(parsed);
        return true;
    }

    static Version Normalize(Version? v) =>
        v == null ? new Version(0, 0, 0) : new Version(v.Major, Math.Max(0, v.Minor), Math.Max(0, v.Build));

    /// <summary>릴리스 노트(마크다운)를 창에 보여줄 평문으로. 다운로드 방법·해시 같은 아래쪽 안내는 뺀다.</summary>
    static string CleanNotes(string markdown)
    {
        var lines = new List<string>();
        foreach (string raw in markdown.Replace("\r", "").Split('\n'))
        {
            string line = raw.TrimEnd();
            if (line.StartsWith("## ")) continue; // 맨 위 제목
            if (line.StartsWith('#'))
            {
                string heading = line.TrimStart('#').Trim();
                if (heading.Contains("다운로드") || heading.Contains("실행 환경") || heading.Contains("파일 확인")) break;
                if (lines.Count > 0 && lines[^1] != "") lines.Add("");
                lines.Add(heading);
                continue;
            }
            if (line.StartsWith("```")) continue;
            line = Regex.Replace(line, @"\[([^\]]+)\]\([^)]+\)", "$1").Replace("**", "").Replace("`", "");
            if (line.StartsWith("- ") || line.StartsWith("* ")) line = "•  " + line[2..];
            else if (line.StartsWith("  - ")) line = "      – " + line[4..];
            if (line == "" && (lines.Count == 0 || lines[^1] == "")) continue;
            lines.Add(line);
        }
        return string.Join("\n", lines).Trim();
    }

    static string? Str(JsonElement e, string name) =>
        e.TryGetProperty(name, out var p) && p.ValueKind == JsonValueKind.String ? p.GetString() : null;

    static bool Bool(JsonElement e, string name) =>
        e.TryGetProperty(name, out var p) && p.ValueKind == JsonValueKind.True;

    // ───────────────────────── 2) 내려받기 + 검증 ─────────────────────────

    public static async Task<string> DownloadAsync(UpdateInfo info, IProgress<DownloadProgress> progress, CancellationToken ct)
    {
        if (!info.CanAutoUpdate) throw new UpdateException("자동 업데이트 파일이 없는 릴리스입니다. 릴리스 페이지에서 직접 내려받아 주세요.");

        PrepareStaging(info.Size);
        string zip = Path.Combine(StagingFolder, info.AssetName!);
        try
        {
            using var response = await Http.GetAsync(info.DownloadUrl, HttpCompletionOption.ResponseHeadersRead, ct);
            response.EnsureSuccessStatusCode();
            long total = response.Content.Headers.ContentLength ?? info.Size;

            using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            await using (var input = await response.Content.ReadAsStreamAsync(ct))
            await using (var output = new FileStream(zip, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16, useAsync: true))
            {
                var buffer = new byte[1 << 17];
                long done = 0, lastReport = 0;
                int read;
                while ((read = await input.ReadAsync(buffer, ct)) > 0)
                {
                    await output.WriteAsync(buffer.AsMemory(0, read), ct);
                    sha.AppendData(buffer, 0, read);
                    done += read;
                    if (done - lastReport >= 1 << 20 || done == total)
                    {
                        progress.Report(new DownloadProgress(done, total));
                        lastReport = done;
                    }
                }
            }

            string actual = Convert.ToHexString(sha.GetHashAndReset());
            if (!actual.Equals(info.Sha256, StringComparison.OrdinalIgnoreCase))
                throw new UpdateException("내려받은 파일이 GitHub에 올라간 원본과 다릅니다(SHA-256 불일치).\n안전을 위해 설치하지 않았습니다. 잠시 뒤 다시 시도해 주세요.");
            return zip;
        }
        catch (HttpRequestException ex)
        {
            throw new UpdateException("새 버전을 내려받지 못했습니다. 인터넷 연결을 확인해 주세요.\n" + ex.Message);
        }
    }

    static void PrepareStaging(long zipSize)
    {
        try
        {
            if (Directory.Exists(StagingFolder)) Directory.Delete(StagingFolder, true);
            var dir = Directory.CreateDirectory(StagingFolder);
            dir.Attributes |= FileAttributes.Hidden;
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
        {
            throw new UpdateException($"프로그램 폴더에 파일을 쓸 수 없어 자동 업데이트를 할 수 없습니다.\n({AppFolder})\n릴리스 페이지에서 직접 내려받아 주세요.");
        }

        // zip + 압축 푼 파일이 함께 놓일 공간 (FFmpeg 포함 약 2.5배)
        long need = Math.Max(zipSize, 200L << 20) * 4;
        try
        {
            var drive = new DriveInfo(Path.GetPathRoot(AppFolder)!);
            if (drive.AvailableFreeSpace < need)
                throw new UpdateException($"{drive.Name} 드라이브 공간이 부족합니다. 약 {need >> 20:N0} MB가 필요합니다.");
        }
        catch (ArgumentException) { /* 네트워크 경로 등은 확인 생략 */ }
    }

    // ───────────────────────── 3) 설치 (파일 교체) ─────────────────────────

    /// <summary>압축을 풀고 바뀐 파일만 교체한다. 필수 파일 교체에 실패하면 모두 되돌리고 예외.</summary>
    public static void Install(string zip)
    {
        string extracted = Path.Combine(StagingFolder, "files");
        ZipFile.ExtractToDirectory(zip, extracted, overwriteFiles: true); // zip 밖 경로로 풀리는 항목은 .NET이 막아 줌
        string newRoot = Directory.EnumerateFiles(extracted, ExeName, SearchOption.AllDirectories)
                             .Select(Path.GetDirectoryName).FirstOrDefault()
                         ?? throw new UpdateException("내려받은 파일에 프로그램이 들어 있지 않습니다.");

        var replaced = new List<(string Target, bool HadOld)>();
        try
        {
            foreach (string source in Directory.EnumerateFiles(newRoot!, "*", SearchOption.AllDirectories))
            {
                string relative = Path.GetRelativePath(newRoot!, source);
                string target = Path.Combine(AppFolder, relative);
                try
                {
                    if (File.Exists(target) && SameContent(source, target)) continue;
                    bool hadOld = Replace(source, target);
                    replaced.Add((target, hadOld));
                }
                catch (Exception) when (!IsEssential(relative))
                {
                    // 사용 가이드 PDF가 열려 있는 경우 등은 건너뛴다 (프로그램 동작과 무관)
                }
            }
        }
        catch (Exception ex)
        {
            Rollback(replaced);
            throw new UpdateException("파일을 바꾸지 못해 원래 버전으로 되돌렸습니다.\n" + ex.Message);
        }
        finally
        {
            TryDelete(StagingFolder);
        }
    }

    static bool IsEssential(string relative)
    {
        string name = Path.GetFileName(relative);
        return name.Equals(ExeName, StringComparison.OrdinalIgnoreCase)
            || name.Equals("ffmpeg.exe", StringComparison.OrdinalIgnoreCase)
            || name.Equals("ffprobe.exe", StringComparison.OrdinalIgnoreCase);
    }

    /// <returns>기존 파일이 있어서 *.tlc-old로 옮겨 뒀으면 true</returns>
    static bool Replace(string source, string target)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        bool hadOld = File.Exists(target);
        if (hadOld) File.Move(target, target + OldSuffix, overwrite: true); // 실행 중인 exe도 이름은 바꿀 수 있다
        try
        {
            File.Move(source, target);
        }
        catch
        {
            if (hadOld && !File.Exists(target)) File.Move(target + OldSuffix, target);
            throw;
        }
        return hadOld;
    }

    static void Rollback(List<(string Target, bool HadOld)> replaced)
    {
        for (int i = replaced.Count - 1; i >= 0; i--)
        {
            var (target, hadOld) = replaced[i];
            try
            {
                File.Delete(target);
                if (hadOld) File.Move(target + OldSuffix, target);
            }
            catch { /* 최대한 되돌린다 */ }
        }
    }

    static bool SameContent(string a, string b)
    {
        if (new FileInfo(a).Length != new FileInfo(b).Length) return false;
        using var sa = File.OpenRead(a);
        using var sb = new FileStream(b, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        return SHA256.HashData(sa).AsSpan().SequenceEqual(SHA256.HashData(sb));
    }

    /// <summary>업데이트 중 취소·실패했을 때 내려받던 파일 정리</summary>
    public static void DiscardStaging() => TryDelete(StagingFolder);

    static void TryDelete(string folder)
    {
        try { if (Directory.Exists(folder)) Directory.Delete(folder, true); } catch { }
    }

    // ───────────────────────── 4) 재시작 · 정리 ─────────────────────────

    public static void StartNewVersion()
    {
        Process.Start(new ProcessStartInfo(Path.Combine(AppFolder, ExeName))
        {
            UseShellExecute = false,
            WorkingDirectory = AppFolder,
            Arguments = $"--updated-from {CurrentVersion.ToString(3)}",
        });
    }

    /// <summary>
    /// 이전 버전이 남긴 *.tlc-old 파일과 임시 폴더를 지운다.
    /// 이전 프로세스가 아직 종료 중일 수 있어 몇 초 동안 다시 시도한다. (프로그램 폴더와 ffmpeg 폴더만)
    /// </summary>
    public static void CleanupLeftoversInBackground()
    {
        _ = Task.Run(async () =>
        {
            TryDelete(StagingFolder); // 지난번에 중단된 내려받기 (한 번만: 곧바로 새 업데이트를 시작할 수도 있으므로)
            for (int attempt = 0; attempt < 20; attempt++)
            {
                bool remaining = false;
                foreach (string folder in new[] { AppFolder, Path.Combine(AppFolder, "ffmpeg") })
                {
                    try
                    {
                        if (!Directory.Exists(folder)) continue;
                        foreach (string file in Directory.EnumerateFiles(folder, "*" + OldSuffix))
                        {
                            try { File.Delete(file); } catch { remaining = true; }
                        }
                    }
                    catch { }
                }
                if (!remaining) return;
                await Task.Delay(500);
            }
        });
    }
}
