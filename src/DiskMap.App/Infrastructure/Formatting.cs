namespace DiskMap.App.Infrastructure;

public static class Formatting
{
    private static readonly string[] Units = ["B", "KB", "MB", "GB", "TB", "PB"];

    public static string Bytes(long bytes)
    {
        double size = bytes;
        int unit = 0;
        while (size >= 1024 && unit < Units.Length - 1)
        {
            size /= 1024;
            unit++;
        }
        return unit == 0 ? $"{bytes:N0} {Units[unit]}" : $"{size:0.##} {Units[unit]}";
    }

    public static string SignedBytes(long bytes) => (bytes >= 0 ? "+" : "-") + Bytes(Math.Abs(bytes));

    /// <summary>Parses a free-typed size like "500", "500 KB", "1.5GB" into bytes. Bare numbers
    /// are treated as MB — the unit a user filtering disk usage almost always means.</summary>
    public static bool TryParseSize(string text, out long bytes)
    {
        bytes = 0;
        text = text.Trim();
        if (text.Length == 0) return false;

        int splitAt = text.Length;
        while (splitAt > 0 && !char.IsDigit(text[splitAt - 1]) && text[splitAt - 1] != '.') splitAt--;

        string numberPart = text[..splitAt];
        string unitPart = text[splitAt..].Trim().ToUpperInvariant();
        if (!double.TryParse(numberPart, System.Globalization.CultureInfo.InvariantCulture, out double value))
            return false;

        double multiplier = unitPart switch
        {
            "" or "MB" or "M" => 1024.0 * 1024,
            "B" => 1,
            "KB" or "K" => 1024,
            "GB" or "G" => 1024.0 * 1024 * 1024,
            "TB" or "T" => 1024.0 * 1024 * 1024 * 1024,
            _ => -1,
        };
        if (multiplier < 0) return false;

        bytes = (long)(value * multiplier);
        return true;
    }

    /// <summary>Short flag-letter summary of Win32 FILE_ATTRIBUTE_* bits, e.g. "RHS" or "A".</summary>
    public static string FileAttributesText(uint attributes)
    {
        if (attributes == 0) return "";
        var letters = new System.Text.StringBuilder();
        if ((attributes & 0x1) != 0) letters.Append('R');    // READONLY
        if ((attributes & 0x2) != 0) letters.Append('H');    // HIDDEN
        if ((attributes & 0x4) != 0) letters.Append('S');    // SYSTEM
        if ((attributes & 0x20) != 0) letters.Append('A');   // ARCHIVE
        if ((attributes & 0x800) != 0) letters.Append('C');  // COMPRESSED
        if ((attributes & 0x4000) != 0) letters.Append('E'); // ENCRYPTED
        if ((attributes & 0x400) != 0) letters.Append('L');  // REPARSE_POINT (link/junction)
        return letters.ToString();
    }
}
