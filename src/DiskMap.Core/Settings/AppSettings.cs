using System.Text.Json;
using System.Text.Json.Serialization;

namespace DiskMap.Core.Settings;

/// <summary>How aggressively the recursive scanner uses the thread pool and system I/O priority.</summary>
public enum ScanImpactMode
{
    /// <summary>Full parallelism — <c>ProcessorCount</c> workers, normal priority. Use when the machine is idle.</summary>
    Fast,
    /// <summary>Half the cores (min 2). The default — responsive without saturating the box.</summary>
    Balanced,
    /// <summary>Two workers plus background I/O priority so a scan doesn't compete with foreground work.</summary>
    Low,
}

/// <summary>How reparse points (junctions, directory symlinks) are treated during a recursive scan.</summary>
public enum ReparseBehavior
{
    /// <summary>Reparse points are not listed at all.</summary>
    Ignore,
    /// <summary>Listed as nodes but never followed — the safe default, avoids cycles and double counting.</summary>
    Show,
    /// <summary>Followed into the target, guarded by file-ID cycle detection so A→B→A can't recurse forever.</summary>
    Follow,
}

/// <summary>
/// User preferences persisted as JSON under <c>%LOCALAPPDATA%\DiskMap\settings.json</c>. Kept tiny on
/// purpose — this is a local-first, no-telemetry app, so only genuinely user-facing knobs live here.
/// </summary>
public sealed class AppSettings
{
    public string Theme { get; set; } = "System";
    public bool HasSeenWelcome { get; set; }

    /// <summary>Thread/I-O throttle mode for recursive scans (Fast/Balanced/Low). MFT scans ignore this.</summary>
    public ScanImpactMode ScanMode { get; set; } = ScanImpactMode.Balanced;

    /// <summary>How junctions and directory symlinks are handled during a recursive scan.</summary>
    public ReparseBehavior ReparseBehavior { get; set; } = ReparseBehavior.Show;

    /// <summary>When true, the app relaunches itself elevated on startup (triggers a UAC prompt each launch).
    /// Off by default — DiskMap never requires admin. Only opted-in users pay the UAC cost, in exchange
    /// for whole-drive NTFS $MFT scanning being available immediately without a manual button click.</summary>
    public bool AlwaysElevated { get; set; }

    /// <summary>When true, the recursive scanner writes a checkpoint periodically and offers to resume an
    /// interrupted scan of the same root instead of starting over.</summary>
    public bool CrashSafeResume { get; set; } = true;

    /// <summary>When true, fully-aggregated directories the user hasn't expanded are trimmed to their largest
    /// children to bound peak memory on huge (20M+ file) drives. Off by default to preserve full fidelity.</summary>
    public bool MemorySaver { get; set; }

    /// <summary>The last path the user scanned, so the app can default there next launch.</summary>
    public string? LastScannedPath { get; set; }

    /// <summary>Minimum file size (bytes) for a file to be considered in duplicate detection.</summary>
    public long MinDuplicateSize { get; set; } = 1;

    private static string FilePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DiskMap", "settings.json");

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = true,
    };

    public static AppSettings Load()
    {
        try
        {
            if (File.Exists(FilePath))
                return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(FilePath), JsonOptions) ?? new AppSettings();
        }
        catch (IOException) { }
        catch (JsonException) { }
        return new AppSettings();
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(this, JsonOptions));
        }
        catch (IOException) { }
    }

    /// <summary>The maximum number of concurrent directory-walk workers for the chosen impact mode.</summary>
    public static int DegreeOfParallelism(ScanImpactMode mode) => mode switch
    {
        ScanImpactMode.Fast => Math.Max(1, Environment.ProcessorCount),
        ScanImpactMode.Low => 2,
        _ => Math.Max(2, Environment.ProcessorCount / 2),
    };
}
