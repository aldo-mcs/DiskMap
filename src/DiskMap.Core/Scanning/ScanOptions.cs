using DiskMap.Core.Settings;

namespace DiskMap.Core.Scanning;

/// <summary>
/// Knobs passed into <see cref="DirectoryScanner.Scan(string, ScanOptions?, IProgress{ScanProgress}?, CancellationToken)"/>.
/// Defaults mirror the historical behavior (balanced parallelism, reparse points shown but not followed) so that
/// callers that pass nothing get the same scan they always did.
/// </summary>
public sealed class ScanOptions
{
    /// <summary>How aggressively to use the thread pool and system I/O priority. Defaults to <see cref="ScanImpactMode.Balanced"/>.</summary>
    public ScanImpactMode ImpactMode { get; init; } = ScanImpactMode.Balanced;

    /// <summary>How junctions and directory symlinks are handled. Defaults to <see cref="ReparseBehavior.Show"/>.</summary>
    public ReparseBehavior ReparseBehavior { get; init; } = ReparseBehavior.Show;

    /// <summary>When true, a checkpoint is written periodically so an interrupted scan can be resumed.</summary>
    public bool EnableResume { get; init; } = true;

    /// <summary>When true, fully-aggregated unexpanded directories are trimmed to their top children to bound memory.</summary>
    public bool MemorySaver { get; init; }

    /// <summary>The root path being scanned, used to name the checkpoint file. Set by the scanner.</summary>
    internal string RootPath { get; init; } = "";

    /// <summary>Convenience: builds options from persisted user settings.</summary>
    public static ScanOptions FromSettings(AppSettings s, string rootPath) => new()
    {
        ImpactMode = s.ScanMode,
        ReparseBehavior = s.ReparseBehavior,
        EnableResume = s.CrashSafeResume,
        MemorySaver = s.MemorySaver,
        RootPath = rootPath,
    };
}
