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

    /// <summary>배속 표시: x256 / x281 / x2.5</summary>
    public static string Speed(double speed)
    {
        double rounded = Math.Round(speed);
        if (Math.Abs(speed - rounded) < 0.05 || speed >= 10) return $"x{rounded:0}";
        return $"x{speed:0.#}";
    }

    /// <summary>구간 입력용 시각: "1:08:40" (1시간 이상 영상) / "8:40"</summary>
    public static string Timecode(double seconds, bool hours)
    {
        long s = (long)Math.Round(Math.Max(0, seconds));
        return hours || s >= 3600
            ? $"{s / 3600}:{s % 3600 / 60:00}:{s % 60:00}"
            : $"{s / 60}:{s % 60:00}";
    }

    /// <summary>"1:08:40", "8:40", "520", "8:40.5" 형식을 초로 읽는다</summary>
    public static bool TryParseTimecode(string text, out double seconds)
    {
        seconds = 0;
        var parts = text.Trim().Replace('：', ':').Split(':');
        if (parts.Length is < 1 or > 3) return false;
        double total = 0;
        for (int i = 0; i < parts.Length; i++)
        {
            bool last = i == parts.Length - 1;
            if (!double.TryParse(parts[i].Trim(), System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out double v) || v < 0)
                return false;
            if (!last && v != Math.Floor(v)) return false;
            total = total * 60 + v;
        }
        seconds = total;
        return true;
    }

    /// <summary>
    /// WPF는 한글을 글자 단위로 줄바꿈해서 "보/여 줍니다"처럼 끊긴다.
    /// 한글 글자 사이에 보이지 않는 WORD JOINER(U+2060)를 넣어 띄어쓰기에서만 줄이 바뀌게 한다.
    /// </summary>
    public static string KeepWords(string text)
    {
        var sb = new System.Text.StringBuilder(text.Length * 2);
        for (int i = 0; i < text.Length; i++)
        {
            sb.Append(text[i]);
            if (i + 1 < text.Length && (IsHangul(text[i]) || IsHangul(text[i + 1]))
                && !char.IsWhiteSpace(text[i]) && !char.IsWhiteSpace(text[i + 1]))
                sb.Append('⁠');
        }
        return sb.ToString();
    }

    static bool IsHangul(char c) => c is >= '가' and <= '힣';

    public static string Size(long bytes)
    {
        const double KB = 1024, MB = KB * 1024, GB = MB * 1024;
        if (bytes >= GB) return $"{bytes / GB:0.0} GB";
        if (bytes >= MB) return $"{bytes / MB:0.0} MB";
        return $"{bytes / KB:0.0} KB";
    }
}
