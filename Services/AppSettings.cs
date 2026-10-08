using System.IO;
using System.Text.Json;
using TimelapseConverter.Helpers;

namespace TimelapseConverter.Services;

/// <summary>
/// 사용자 설정 (%APPDATA%\TimelapseConverter\settings.json)
/// </summary>
public sealed class AppSettings
{
    public string SourceFolder { get; set; } = "";
    public string OutputFolder { get; set; } = "";
    public string? FfmpegFolder { get; set; }
    public bool UseGpu { get; set; } = true;
    public int SpeedIndex { get; set; } = 6; // x64

    static string FilePath => Path.Combine(AppPaths.DataFolder, "settings.json");

    public static AppSettings Load()
    {
        AppSettings settings;
        try
        {
            settings = File.Exists(FilePath)
                ? JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(FilePath)) ?? new AppSettings()
                : new AppSettings();
        }
        catch
        {
            settings = new AppSettings();
        }

        if (string.IsNullOrWhiteSpace(settings.SourceFolder)) settings.SourceFolder = DefaultSourceFolder();
        if (string.IsNullOrWhiteSpace(settings.OutputFolder)) settings.OutputFolder = DefaultOutputFolder();
        return settings;
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(AppPaths.DataFolder);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch { /* 설정 저장 실패는 치명적이지 않음 */ }
    }

    // 웹 버전(application.properties)의 녹화 폴더가 있으면 그대로, 없으면 OBS 기본 녹화 위치인 내 비디오 폴더
    static string DefaultSourceFolder() =>
        Directory.Exists(@"D:\OBS\recording")
            ? @"D:\OBS\recording"
            : Environment.GetFolderPath(Environment.SpecialFolder.MyVideos);

    public static string DefaultOutputFolder() => AppPaths.DefaultOutputFolder;
}
