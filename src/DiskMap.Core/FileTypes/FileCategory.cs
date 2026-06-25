using System.Collections.Concurrent;

namespace DiskMap.Core.FileTypes;

/// <summary>High-level grouping of file types, used for both analytics rollups and color assignment so
/// the legend and the "Videos 42%" breakdown always agree. Single source of truth — the treemap colors
/// in <c>FileTypeColors</c> are derived from this rather than maintained separately.</summary>
public enum FileCategory
{
    Video,
    Image,
    Audio,
    Archives,
    Documents,
    Code,
    Executables,
    Games,
    System,
    Other,
}

/// <summary>Stable per-category hue (0..359) used to derive both the analytics swatch and the treemap color,
/// so a category looks the same everywhere. Saturation/value are applied per-theme by the color layer.</summary>
public static class FileCategoryMeta
{
    public static double Hue(FileCategory c) => c switch
    {
        FileCategory.Video => 212,       // blue
        FileCategory.Image => 130,       // green
        FileCategory.Audio => 30,        // orange
        FileCategory.Archives => 265,    // purple
        FileCategory.Documents => 200,   // teal-blue
        FileCategory.Code => 150,        // teal-green
        FileCategory.Executables => 0,   // red
        FileCategory.Games => 290,       // magenta
        FileCategory.System => 220,      // slate
        _ => 0,
    };

    /// <summary>Human label matching the enum name but prettier for the UI.</summary>
    public static string Label(FileCategory c) => c switch
    {
        FileCategory.Video => "Video",
        FileCategory.Image => "Image",
        FileCategory.Audio => "Audio",
        FileCategory.Archives => "Archives",
        FileCategory.Documents => "Documents",
        FileCategory.Code => "Code",
        FileCategory.Executables => "Executables",
        FileCategory.Games => "Games",
        FileCategory.System => "System",
        _ => "Other",
    };
}

/// <summary>Maps a file extension (with leading dot, lower-cased) to a <see cref="FileCategory"/>.
/// Falls back to <see cref="FileCategory.Other"/> for anything uncurated.</summary>
public static class Categorizer
{
    private static readonly Dictionary<string, FileCategory> Map = new(StringComparer.OrdinalIgnoreCase)
    {
        // Video
        [".mp4"] = FileCategory.Video, [".mkv"] = FileCategory.Video, [".avi"] = FileCategory.Video,
        [".mov"] = FileCategory.Video, [".wmv"] = FileCategory.Video, [".webm"] = FileCategory.Video,
        [".flv"] = FileCategory.Video, [".m4v"] = FileCategory.Video, [".mpg"] = FileCategory.Video,
        [".mpeg"] = FileCategory.Video, [".3gp"] = FileCategory.Video, [".vob"] = FileCategory.Video,
        // Image
        [".jpg"] = FileCategory.Image, [".jpeg"] = FileCategory.Image, [".png"] = FileCategory.Image,
        [".gif"] = FileCategory.Image, [".bmp"] = FileCategory.Image, [".webp"] = FileCategory.Image,
        [".tiff"] = FileCategory.Image, [".tif"] = FileCategory.Image, [".heic"] = FileCategory.Image,
        [".svg"] = FileCategory.Image, [".raw"] = FileCategory.Image, [".psd"] = FileCategory.Image,
        // Audio
        [".mp3"] = FileCategory.Audio, [".wav"] = FileCategory.Audio, [".flac"] = FileCategory.Audio,
        [".aac"] = FileCategory.Audio, [".ogg"] = FileCategory.Audio, [".m4a"] = FileCategory.Audio,
        [".wma"] = FileCategory.Audio, [".aiff"] = FileCategory.Audio, [".opus"] = FileCategory.Audio,
        // Archives
        [".zip"] = FileCategory.Archives, [".rar"] = FileCategory.Archives, [".7z"] = FileCategory.Archives,
        [".tar"] = FileCategory.Archives, [".gz"] = FileCategory.Archives, [".iso"] = FileCategory.Archives,
        [".bz2"] = FileCategory.Archives, [".xz"] = FileCategory.Archives, [".cab"] = FileCategory.Archives,
        [".dmg"] = FileCategory.Archives, [".tgz"] = FileCategory.Archives,
        // Documents
        [".pdf"] = FileCategory.Documents, [".doc"] = FileCategory.Documents, [".docx"] = FileCategory.Documents,
        [".xls"] = FileCategory.Documents, [".xlsx"] = FileCategory.Documents, [".ppt"] = FileCategory.Documents,
        [".pptx"] = FileCategory.Documents, [".txt"] = FileCategory.Documents, [".md"] = FileCategory.Documents,
        [".rtf"] = FileCategory.Documents, [".odt"] = FileCategory.Documents, [".csv"] = FileCategory.Documents,
        [".epub"] = FileCategory.Documents, [".pages"] = FileCategory.Documents,
        // Code
        [".cs"] = FileCategory.Code, [".js"] = FileCategory.Code, [".ts"] = FileCategory.Code,
        [".py"] = FileCategory.Code, [".json"] = FileCategory.Code, [".xml"] = FileCategory.Code,
        [".html"] = FileCategory.Code, [".css"] = FileCategory.Code, [".java"] = FileCategory.Code,
        [".cpp"] = FileCategory.Code, [".c"] = FileCategory.Code, [".h"] = FileCategory.Code,
        [".go"] = FileCategory.Code, [".rs"] = FileCategory.Code, [".rb"] = FileCategory.Code,
        [".php"] = FileCategory.Code, [".sh"] = FileCategory.Code, [".sql"] = FileCategory.Code,
        [".yml"] = FileCategory.Code, [".yaml"] = FileCategory.Code, [".tsx"] = FileCategory.Code,
        [".jsx"] = FileCategory.Code,
        // Executables / binaries
        [".exe"] = FileCategory.Executables, [".dll"] = FileCategory.Executables, [".msi"] = FileCategory.Executables,
        [".bin"] = FileCategory.Executables, [".sys"] = FileCategory.System, [".bat"] = FileCategory.Executables,
        [".cmd"] = FileCategory.Executables, [".ps1"] = FileCategory.Executables, [".app"] = FileCategory.Executables,
        [".jar"] = FileCategory.Executables, [".com"] = FileCategory.Executables, [".scr"] = FileCategory.Executables,
        // Game data (common packing/container formats; paths refine this further)
        [".pak"] = FileCategory.Games, [".bsa"] = FileCategory.Games, [".cas"] = FileCategory.Games,
        [".vpk"] = FileCategory.Games, [".uasset"] = FileCategory.Games, [".uexp"] = FileCategory.Games,
        [".bnk"] = FileCategory.Games, [".wem"] = FileCategory.Games, [".forge"] = FileCategory.Games,
        [".rpf"] = FileCategory.Games, [".sims4"] = FileCategory.Games,
    };

    private static readonly ConcurrentDictionary<string, FileCategory> Cache = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Category for an extension (with or without the leading dot). "" → <see cref="FileCategory.Other"/>.</summary>
    public static FileCategory OfExtension(string? extension)
    {
        if (string.IsNullOrEmpty(extension) || extension == "(no extension)") return FileCategory.Other;
        return Cache.GetOrAdd(extension, e =>
        {
            string key = e.StartsWith('.') ? e.ToLowerInvariant() : "." + e.ToLowerInvariant();
            return Map.TryGetValue(key, out var c) ? c : FileCategory.Other;
        });
    }

    /// <summary>Refines a base category using the file's full path. Most importantly, promotes executables
    /// and archives that live under a games library (steamapps, Epic, WindowsApps, ...) to <see cref="FileCategory.Games"/>,
    /// which a pure extension lookup can't catch.</summary>
    public static FileCategory RefineByPath(FileCategory baseCategory, string fullPath)
    {
        if (baseCategory is FileCategory.Executables or FileCategory.Archives or FileCategory.Other or FileCategory.Code or FileCategory.Documents
            && IsGamesLibraryPath(fullPath))
        {
            return FileCategory.Games;
        }
        return baseCategory;
    }

    /// <summary>True if the path looks like a game-install library. Case-insensitive substring match on common roots.</summary>
    public static bool IsGamesLibraryPath(string fullPath)
    {
        if (string.IsNullOrEmpty(fullPath)) return false;
        ReadOnlySpan<char> p = fullPath.AsSpan();
        return p.Contains("steamapps", StringComparison.OrdinalIgnoreCase)
            || p.Contains("epic", StringComparison.OrdinalIgnoreCase)
            || p.Contains("windowsapps", StringComparison.OrdinalIgnoreCase)
            || p.Contains("\\games\\", StringComparison.OrdinalIgnoreCase)
            || p.Contains("\\gog galaxy", StringComparison.OrdinalIgnoreCase)
            || p.Contains("\\origin games", StringComparison.OrdinalIgnoreCase)
            || p.Contains("\\ubisoft", StringComparison.OrdinalIgnoreCase)
            || p.Contains("\\battle.net", StringComparison.OrdinalIgnoreCase)
            || p.Contains("\\xboxgames", StringComparison.OrdinalIgnoreCase);
    }
}
