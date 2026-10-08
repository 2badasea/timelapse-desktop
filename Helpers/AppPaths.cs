using System.IO;

namespace TimelapseConverter.Helpers;

static class AppPaths
{
    /// <summary>설정 파일 위치 (%APPDATA%\TimelapseConverter)</summary>
    public static string DataFolder =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "TimelapseConverter");

    /// <summary>
    /// 기본 작업 폴더: D 드라이브(고정 디스크)가 있으면 D:\TimeLapse, 없으면 C:\TimeLapse.
    /// USB·광학 드라이브가 D:로 잡힌 PC도 있어서 고정 디스크인지 확인한다.
    /// </summary>
    public static string DefaultRoot { get; } = IsFixedDrive("D") ? @"D:\TimeLapse" : @"C:\TimeLapse";

    public static string DefaultOutputFolder => Path.Combine(DefaultRoot, "output");

    /// <summary>미리보기 임시 파일 폴더 ({기본 작업 폴더}\temp)</summary>
    public static string TempFolder
    {
        get
        {
            string path = Path.Combine(DefaultRoot, "temp");
            try
            {
                Directory.CreateDirectory(path);
                return path;
            }
            catch
            {
                // 권한 문제 등으로 만들 수 없으면 Windows 임시 폴더 사용
                string fallback = Path.Combine(Path.GetTempPath(), "TimelapseConverter");
                Directory.CreateDirectory(fallback);
                return fallback;
            }
        }
    }

    public static (int Count, long Bytes) TempUsage()
    {
        try
        {
            var files = new DirectoryInfo(TempFolder).GetFiles();
            return (files.Length, files.Sum(f => f.Length));
        }
        catch { return (0, 0); }
    }

    /// <returns>지우지 못한 파일 수 (재생 중인 파일 등)</returns>
    public static int CleanTemp()
    {
        int failed = 0;
        try
        {
            foreach (var file in Directory.EnumerateFiles(TempFolder))
            {
                try { File.Delete(file); } catch { failed++; }
            }
        }
        catch { }
        return failed;
    }

    static bool IsFixedDrive(string letter)
    {
        try
        {
            var drive = new DriveInfo(letter);
            return drive.DriveType == DriveType.Fixed && drive.IsReady;
        }
        catch { return false; }
    }
}
