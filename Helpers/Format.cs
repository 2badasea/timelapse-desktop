namespace TimelapseConverter.Helpers;

static class Format
{
    /// <summary>초 → "1시간 2분 3초" / "1시간" / "2분 3초" / "4.5초" (0인 단위는 생략)</summary>
    public static string Duration(double seconds)
    {
        if (double.IsNaN(seconds) || seconds <= 0) return "--";
        if (seconds < 10) return $"{seconds:0.#}초";

        long s = (long)Math.Round(seconds);
        long h = s / 3600, m = s % 3600 / 60, r = s % 60;
        var parts = new List<string>(3);
        if (h > 0) parts.Add($"{h}시간");
        if (m > 0) parts.Add($"{m}분");
        if (r > 0 || parts.Count == 0) parts.Add($"{r}초");
        return string.Join(" ", parts);
    }

    /// <summary>경과 시간 표시용 "m:ss" / "h:mm:ss"</summary>
    public static string Clock(TimeSpan t) =>
        t.TotalHours >= 1 ? t.ToString(@"h\:mm\:ss") : t.ToString(@"m\:ss");

    public static string Size(long bytes)
    {
        const double KB = 1024, MB = KB * 1024, GB = MB * 1024;
        if (bytes >= GB) return $"{bytes / GB:0.0} GB";
        if (bytes >= MB) return $"{bytes / MB:0.0} MB";
        return $"{bytes / KB:0.0} KB";
    }
}
