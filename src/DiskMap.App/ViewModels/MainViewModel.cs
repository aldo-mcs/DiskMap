using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DiskMap.App.Infrastructure;
using DiskMap.Core.Actions;
using DiskMap.Core.Cleanup;
using DiskMap.Core.Duplicates;
using DiskMap.Core.FileTypes;
using DiskMap.Core.Scanning;
using DiskMap.Core.Scanning.Mft;
using DiskMap.Core.Settings;
using DiskMap.Core.Snapshots;

namespace DiskMap.App.ViewModels;

public sealed partial class MainViewModel : ObservableObject
{
    private readonly DirectoryScanner _scanner = new();
    private readonly DuplicateFinder _duplicateFinder = new();
    private readonly Lazy<SnapshotStore> _store = new(() => new SnapshotStore());

    private FileSystemNode? _scanRoot;
    private readonly HashSet<FileSystemNode> _expandedNodes = [];
    private CancellationTokenSource? _cts;
    private bool _isSearchMode;
    private bool _syncingSelection;

    /// <summary>Set true during bulk row rebuilds so expand/collapse handlers don't fire.</summary>
    internal bool BulkUpdating { get; private set; }

    public ObservableCollection<NodeViewModel> Rows { get; } = [];
    public ObservableCollection<ExtensionStatViewModel> Extensions { get; } = [];
    public ObservableCollection<DuplicateGroupViewModel> DuplicateGroups { get; } = [];
    public ObservableCollection<FileSystemNode> Breadcrumb { get; } = [];
    public ObservableCollection<string> Drives { get; } = [];
    public ObservableCollection<CleanupCategoryViewModel> CleanupCategories { get; } = [];

    public event Action<NodeViewModel>? ScrollToRowRequested;
    public event Action<string?>? ShowHistoryRequested;

    /// <summary>(title, message, confirmButtonText) -> true if the user confirmed. Handled by
    /// the view via a themed dialog — the ViewModel never shows UI directly.</summary>
    public event Func<string, string, string, bool>? ConfirmRequested;

    [ObservableProperty] private NodeViewModel? _selectedRow;
    [ObservableProperty] private FileSystemNode? _treemapRoot;
    [ObservableProperty] private FileSystemNode? _treemapSelectedNode;
    [ObservableProperty] private bool _isSunburstView;
    public bool IsTreemapView => !IsSunburstView;
    partial void OnIsSunburstViewChanged(bool value) => OnPropertyChanged(nameof(IsTreemapView));
    [ObservableProperty] private string _statusText = "Pick a folder or drive to scan.";
    [ObservableProperty] private string _progressText = string.Empty;
    [ObservableProperty] private string _totalsText = string.Empty;
    [ObservableProperty] private bool _isScanning;
    [ObservableProperty] private bool _isFindingDuplicates;
    [ObservableProperty] private string _searchText = string.Empty;
    [ObservableProperty] private string _rootTitle = "DiskMap";

    // Filters: file type (via Extensions' own IsSelected) + size range. Combined with SearchText
    // (AND) through the same flat-match pipeline search already uses.
    [ObservableProperty] private bool _isFiltersOpen;
    [ObservableProperty] private string _minSizeText = string.Empty;
    [ObservableProperty] private string _maxSizeText = string.Empty;
    public bool HasActiveFilters => Extensions.Any(e => e.IsSelected) || !string.IsNullOrWhiteSpace(MinSizeText) || !string.IsNullOrWhiteSpace(MaxSizeText);

    // Stat chips shown in the dashboard header after a scan.
    [ObservableProperty] private bool _hasScan;
    [ObservableProperty] private string _totalSizeText = "—";
    [ObservableProperty] private string _fileCountText = "—";
    [ObservableProperty] private string _onDiskText = "—";
    [ObservableProperty] private string _largestText = "—";

    // Advanced-mode technical detail, populated after each scan.
    [ObservableProperty] private bool _isAdvancedMode;
    [ObservableProperty] private string _scanMethodText = "—";
    [ObservableProperty] private string _scanTimeText = "—";
    [ObservableProperty] private string _slackText = "—";
    [ObservableProperty] private string _filesystemText = "—";
    [ObservableProperty] private string _clusterSizeText = "—";
    [ObservableProperty] private string _volumeCapacityText = "—";
    [ObservableProperty] private string _mftDiagnosticsText = "—";
    [ObservableProperty] private string _engineDiagnosticsText = "—";

    // Cleanup panel.
    [ObservableProperty] private int _selectedTabIndex;
    [ObservableProperty] private bool _isScanningCleanup;
    [ObservableProperty] private bool _isCleaning;
    [ObservableProperty] private string _cleanupTotalText = "—";
    [ObservableProperty] private string _cleanupStatusText = "Click \"Scan for Junk\" to find reclaimable space.";

    public bool IsElevated { get; } = ElevationHelper.IsElevated();

    // Theme menu radio state. Live (no restart) — refreshed via ThemeManager.ThemeChanged below.
    public bool IsThemeSystem => Infrastructure.ThemeManager.Current == Infrastructure.AppTheme.System;
    public bool IsThemeLight => Infrastructure.ThemeManager.Current == Infrastructure.AppTheme.Light;
    public bool IsThemeDark => Infrastructure.ThemeManager.Current == Infrastructure.AppTheme.Dark;

    public MainViewModel()
    {
        foreach (var drive in DriveInfo.GetDrives())
        {
            try { if (drive.IsReady && (drive.DriveType is DriveType.Fixed or DriveType.Removable)) Drives.Add(drive.Name); }
            catch { /* skip */ }
        }

        Infrastructure.ThemeManager.ThemeChanged += () =>
        {
            OnPropertyChanged(nameof(IsThemeSystem));
            OnPropertyChanged(nameof(IsThemeLight));
            OnPropertyChanged(nameof(IsThemeDark));
        };
    }

    [RelayCommand]
    private void RestartAsAdmin() => ElevationHelper.RestartElevated();

    // ---------------- Scanning ----------------

    [RelayCommand]
    private async Task ScanFolderAsync()
    {
        var dialog = new Microsoft.Win32.OpenFolderDialog { Title = "Select a folder to scan" };
        if (dialog.ShowDialog() == true)
            await ScanAsync(dialog.FolderName);
    }

    [RelayCommand]
    private Task ScanDriveAsync(string root) => ScanAsync(root);

    [RelayCommand(CanExecute = nameof(CanRescan))]
    private async Task RescanAsync()
    {
        if (_scanRoot is not null)
            await ScanAsync(_scanRoot.Path);
    }

    private bool CanRescan() => _scanRoot is not null && !IsScanning;

    [RelayCommand(CanExecute = nameof(CanCancel))]
    private void Cancel() => _cts?.Cancel();

    private bool CanCancel() => IsScanning;

    private async Task ScanAsync(string path)
    {
        if (IsScanning) return;

        _cts = new CancellationTokenSource();
        IsScanning = true;
        SearchText = string.Empty;
        MinSizeText = string.Empty;
        MaxSizeText = string.Empty;
        StatusText = $"Scanning {path} ...";
        DuplicateGroups.Clear();
        UpdateCommandStates();

        var progress = new Progress<ScanProgress>(p =>
            ProgressText = $"{p.FilesScanned:N0} files · {Formatting.Bytes(p.BytesScanned)} · {p.CurrentPath}");

        try
        {
            var token = _cts.Token;
            var stopwatch = System.Diagnostics.Stopwatch.StartNew();
            var (root, usedFastScan, mftDiagnostics) = await Task.Run(() => ScanFastestAvailable(path, progress, token), token);
            stopwatch.Stop();
            LoadResult(root, usedFastScan, stopwatch.Elapsed, mftDiagnostics);

            // Persist a snapshot in the background for the history/diff feature.
            _ = Task.Run(() =>
            {
                try { _store.Value.Save(root); } catch { /* history is best-effort */ }
            });
        }
        catch (OperationCanceledException)
        {
            StatusText = "Scan cancelled.";
        }
        catch (Exception ex)
        {
            StatusText = $"Scan failed: {ex.Message}";
        }
        finally
        {
            IsScanning = false;
            ProgressText = string.Empty;
            _cts?.Dispose();
            _cts = null;
            UpdateCommandStates();
        }
    }

    /// <summary>
    /// Tries a direct NTFS $MFT read first (near-instant for whole-volume scans, requires admin
    /// elevation) and falls back to the parallel recursive walker on any failure — wrong
    /// filesystem, no elevation, or any parsing hiccup. Never throws for those reasons.
    ///
    /// Only attempted for whole-drive scans: reading the $MFT pays a fixed cost proportional to
    /// the entire volume's file count regardless of what's being scanned, so for an arbitrary
    /// subfolder the plain recursive walk is faster — paying that fixed cost there made small
    /// scans feel slower, not faster.
    /// </summary>
    private (FileSystemNode Root, bool UsedFastScan, MftScanDiagnostics? MftDiagnostics) ScanFastestAvailable(string path, IProgress<ScanProgress> progress, CancellationToken token)
    {
        if (IsElevated && IsDriveRoot(path) &&
            MftVolumeScanner.TryScan(path, progress, token, out var mftRoot, out _, out var diagnostics) && mftRoot is not null)
            return (mftRoot, true, diagnostics);

        var options = ScanOptions.FromSettings(AppSettings.Load(), path);
        return (_scanner.Scan(path, options, progress, token), false, null);
    }

    private static bool IsDriveRoot(string path)
    {
        string? root = Path.GetPathRoot(path);
        return root is not null && string.Equals(path.TrimEnd('\\'), root.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase);
    }

    private void LoadResult(FileSystemNode root, bool usedFastScan, TimeSpan elapsed, MftScanDiagnostics? mftDiagnostics)
    {
        _scanRoot = root;
        RootTitle = $"DiskMap — {root.Path}";
        StatusText = usedFastScan
            ? $"Scan complete (NTFS fast scan): {root.Path}"
            : $"Scan complete: {root.Path}";
        TotalsText = $"{Formatting.Bytes(root.Size)} · {root.FileCount:N0} files · {Formatting.Bytes(root.AllocatedSize)} on disk";

        HasScan = true;
        TotalSizeText = Formatting.Bytes(root.Size);
        FileCountText = $"{root.FileCount:N0}";
        OnDiskText = Formatting.Bytes(root.AllocatedSize);
        var largest = root.Children.Count > 0 ? root.Children[0] : null;
        LargestText = largest is not null ? $"{largest.Name} · {Formatting.Bytes(largest.Size)}" : "—";

        ScanMethodText = usedFastScan ? "NTFS $MFT direct" : "Recursive walk";
        ScanTimeText = elapsed.TotalSeconds < 1 ? $"{elapsed.TotalMilliseconds:N0} ms" : $"{elapsed.TotalSeconds:N1} s";
        SlackText = Formatting.Bytes(root.AllocatedSize - root.Size);
        try
        {
            string driveLetter = (Path.GetPathRoot(root.Path) ?? root.Path).TrimEnd('\\', ':');
            var driveInfo = new DriveInfo(driveLetter);
            FilesystemText = driveInfo.DriveFormat;
            ClusterSizeText = Formatting.Bytes(VolumeInfo.GetClusterSize(root.Path));
            VolumeCapacityText = $"{Formatting.Bytes(driveInfo.TotalSize - driveInfo.AvailableFreeSpace)} used / {Formatting.Bytes(driveInfo.TotalSize)} total";
        }
        catch
        {
            FilesystemText = "—";
            ClusterSizeText = "—";
            VolumeCapacityText = "—";
        }

        MftDiagnosticsText = mftDiagnostics is { } d
            ? $"{d.RecordsParsed:N0} / {d.RecordsAttempted:N0} records parsed · {d.RunCount} run(s) · {Formatting.Bytes(d.MftLengthBytes)} table · {d.MftLengthBytes / 1024.0 / 1024.0 / (d.ReadElapsedMs / 1000.0):N0} MB/s read"
            : "N/A — recursive scan has no $MFT pass";

        var proc = System.Diagnostics.Process.GetCurrentProcess();
        proc.Refresh();
        EngineDiagnosticsText = $"DiskMap working set {Formatting.Bytes(proc.WorkingSet64)} · " +
            $"managed heap {Formatting.Bytes(GC.GetTotalMemory(false))} · " +
            $"GC gen0/1/2 {GC.CollectionCount(0)}/{GC.CollectionCount(1)}/{GC.CollectionCount(2)} · " +
            $"{Environment.ProcessorCount} logical cores";

        _expandedNodes.Clear();
        _expandedNodes.Add(root);
        TreemapRoot = root;
        Breadcrumb.Clear();
        Breadcrumb.Add(root);
        RebuildRows();
        SelectedRow = Rows.Count > 0 ? Rows[0] : null;

        // Extension stats off the UI thread.
        var captured = root;
        _ = Task.Run(() => ExtensionStatsBuilder.Build(captured)).ContinueWith(t =>
        {
            Extensions.Clear();
            foreach (var stat in t.Result)
                Extensions.Add(new ExtensionStatViewModel(stat, captured.Size, this));
        }, TaskScheduler.FromCurrentSynchronizationContext());
    }

    // ---------------- Tree-table (flat rows) ----------------

    private void RebuildRows()
    {
        BulkUpdating = true;
        Rows.Clear();
        if (_scanRoot is not null)
            AddSubtree(_scanRoot, 0);
        BulkUpdating = false;
    }

    private void AddSubtree(FileSystemNode node, int depth)
    {
        var vm = new NodeViewModel(node, this, depth) { IsExpanded = _expandedNodes.Contains(node) };
        Rows.Add(vm);
        if (_expandedNodes.Contains(node))
            foreach (var child in node.Children)
                AddSubtree(child, depth + 1);
    }

    internal void OnRowExpandChanged(NodeViewModel row, bool expanded)
    {
        if (BulkUpdating || _isSearchMode) return;
        if (expanded) Expand(row); else Collapse(row);
    }

    private void Expand(NodeViewModel row)
    {
        if (!_expandedNodes.Add(row.Node)) return;
        int idx = Rows.IndexOf(row);
        if (idx < 0) return;
        int insertAt = idx + 1;
        foreach (var child in row.Node.Children)
            Rows.Insert(insertAt++, new NodeViewModel(child, this, row.Depth + 1));
    }

    private void Collapse(NodeViewModel row)
    {
        _expandedNodes.Remove(row.Node);
        // Remove all following rows deeper than this one, and drop them from the expanded set.
        int idx = Rows.IndexOf(row);
        if (idx < 0) return;
        while (idx + 1 < Rows.Count && Rows[idx + 1].Depth > row.Depth)
        {
            _expandedNodes.Remove(Rows[idx + 1].Node);
            Rows.RemoveAt(idx + 1);
        }
    }

    // ---------------- Selection sync ----------------

    partial void OnSelectedRowChanged(NodeViewModel? value)
    {
        UpdateCommandStates();
        if (_syncingSelection) return;
        _syncingSelection = true;
        TreemapSelectedNode = value?.Node;
        _syncingSelection = false;
    }

    partial void OnTreemapSelectedNodeChanged(FileSystemNode? value)
    {
        if (_syncingSelection) return;
        _syncingSelection = true;
        if (value is not null) SelectNodeInTree(value);
        _syncingSelection = false;
    }

    private void SelectNodeInTree(FileSystemNode node)
    {
        if (_scanRoot is null || _isSearchMode) return;

        // Expand the ancestor chain root..node so the target row is materialized.
        var chain = new List<FileSystemNode>();
        for (var n = node; n is not null; n = n.Parent)
        {
            chain.Add(n);
            if (n == _scanRoot) break;
        }
        chain.Reverse();

        for (int i = 0; i < chain.Count - 1; i++)
        {
            var row = FindRow(chain[i]);
            if (row is null) break;
            if (!row.IsExpanded) row.IsExpanded = true;
        }

        var target = FindRow(node);
        if (target is not null)
        {
            SelectedRow = target;
            ScrollToRowRequested?.Invoke(target);
        }
    }

    private NodeViewModel? FindRow(FileSystemNode node)
    {
        foreach (var row in Rows)
            if (ReferenceEquals(row.Node, node)) return row;
        return null;
    }

    // ---------------- Treemap drill / breadcrumb ----------------

    public void DrillInto(FileSystemNode node)
    {
        if (!node.IsDirectory) return;
        TreemapRoot = node;

        Breadcrumb.Clear();
        var chain = new List<FileSystemNode>();
        for (var n = node; n is not null; n = n.Parent)
        {
            chain.Add(n);
            if (n == _scanRoot) break;
        }
        chain.Reverse();
        foreach (var n in chain) Breadcrumb.Add(n);
    }

    [RelayCommand]
    private void NavigateBreadcrumb(FileSystemNode node) => DrillInto(node);

    // ---------------- Sorting ----------------

    private string _sortKey = "Size";
    private bool _sortDescending = true;

    public void SortBy(string header)
    {
        _sortDescending = header == _sortKey ? !_sortDescending : header != "Name";
        _sortKey = header;
        if (_scanRoot is null) return;

        var comparison = BuildComparison(header, _sortDescending);
        SortChildren(_scanRoot, comparison);

        if (_isSearchMode) _ = ApplyFiltersAndSearchAsync();
        else RebuildRows();
    }

    private static Comparison<FileSystemNode> BuildComparison(string header, bool descending)
    {
        Comparison<FileSystemNode> baseCmp = header switch
        {
            "Name" => static (a, b) => string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase),
            "Files" => static (a, b) => a.FileCount.CompareTo(b.FileCount),
            "Last Modified" => static (a, b) => a.LastModified.CompareTo(b.LastModified),
            "On Disk" => static (a, b) => a.AllocatedSize.CompareTo(b.AllocatedSize),
            _ => static (a, b) => a.Size.CompareTo(b.Size), // Size, %, Subtree
        };
        return descending ? (a, b) => baseCmp(b, a) : baseCmp;
    }

    private static void SortChildren(FileSystemNode node, Comparison<FileSystemNode> comparison)
    {
        if (!node.IsDirectory || node.Children.Count == 0) return;
        node.Children.Sort(comparison);
        foreach (var child in node.Children)
            SortChildren(child, comparison);
    }

    // ---------------- Search ----------------

    private CancellationTokenSource? _searchCts;
    private const int SearchResultCap = 5000;

    // Fire-and-forget from the property-changed hook is intentional: each keystroke/filter edit
    // cancels and supersedes the previous in-flight pass, so only the latest one's result lands.
    partial void OnSearchTextChanged(string value) => _ = ApplyFiltersAndSearchAsync();
    partial void OnMinSizeTextChanged(string value) => OnFiltersChanged();
    partial void OnMaxSizeTextChanged(string value) => OnFiltersChanged();

    /// <summary>Called whenever a filter control changes (size box edited, a file-type checkbox
    /// toggled, or Clear Filters). Public so <see cref="ExtensionStatViewModel"/> can call back
    /// without the filter UI needing its own code-behind.</summary>
    internal void OnFiltersChanged()
    {
        OnPropertyChanged(nameof(HasActiveFilters));
        _ = ApplyFiltersAndSearchAsync();
    }

    private async Task ApplyFiltersAndSearchAsync()
    {
        _searchCts?.Cancel();

        string text = SearchText;
        var selectedExtensions = Extensions.Where(e => e.IsSelected).Select(e => e.Extension).ToHashSet(StringComparer.OrdinalIgnoreCase);
        bool hasMin = Formatting.TryParseSize(MinSizeText, out long minSize);
        bool hasMax = Formatting.TryParseSize(MaxSizeText, out long maxSize);
        bool anyFilter = !string.IsNullOrWhiteSpace(text) || selectedExtensions.Count > 0 || hasMin || hasMax;

        if (!anyFilter)
        {
            if (_isSearchMode)
            {
                _isSearchMode = false;
                RebuildRows();
                StatusText = _scanRoot is not null ? $"Scan complete: {_scanRoot.Path}" : StatusText;
            }
            return;
        }
        if (_scanRoot is null) return;

        var cts = new CancellationTokenSource();
        _searchCts = cts;
        var root = _scanRoot;

        try
        {
            // Tree traversal only reads FileSystemNode — safe to run off the UI thread.
            var matches = await Task.Run(() =>
            {
                var found = new List<FileSystemNode>();
                foreach (var node in EnumerateAll(root))
                {
                    cts.Token.ThrowIfCancellationRequested();
                    if (!string.IsNullOrWhiteSpace(text) && !node.Name.Contains(text, StringComparison.OrdinalIgnoreCase)) continue;
                    if (selectedExtensions.Count > 0 && !selectedExtensions.Contains(node.Extension)) continue;
                    if (hasMin && node.Size < minSize) continue;
                    if (hasMax && node.Size > maxSize) continue;

                    found.Add(node);
                    if (found.Count >= SearchResultCap) break;
                }
                return found;
            }, cts.Token);

            _isSearchMode = true;
            BulkUpdating = true;
            Rows.Clear();
            foreach (var node in matches)
                Rows.Add(new NodeViewModel(node, this, 0));
            BulkUpdating = false;
            StatusText = $"{matches.Count}{(matches.Count >= SearchResultCap ? "+" : "")} matches";
        }
        catch (OperationCanceledException)
        {
            // Superseded by a newer edit; that pass's completion will update the UI instead.
        }
    }

    [RelayCommand]
    private void ClearSearch() => SearchText = string.Empty;

    [RelayCommand]
    private void ClearFilters()
    {
        foreach (var ext in Extensions) ext.IsSelected = false;
        MinSizeText = string.Empty;
        MaxSizeText = string.Empty;
        OnFiltersChanged();
    }

    [RelayCommand]
    private void ToggleFilters() => IsFiltersOpen = !IsFiltersOpen;

    private static IEnumerable<FileSystemNode> EnumerateAll(FileSystemNode root)
    {
        var stack = new Stack<FileSystemNode>();
        stack.Push(root);
        while (stack.Count > 0)
        {
            var n = stack.Pop();
            yield return n;
            for (int i = n.Children.Count - 1; i >= 0; i--)
                stack.Push(n.Children[i]);
        }
    }

    // ---------------- Cleanup actions ----------------

    [RelayCommand(CanExecute = nameof(HasSelection))]
    private void Reveal() => Try(() => FileActions.RevealInExplorer(SelectedRow!.Node.Path));

    [RelayCommand(CanExecute = nameof(HasSelection))]
    private void Open() => Try(() => FileActions.Open(SelectedRow!.Node.Path));

    [RelayCommand(CanExecute = nameof(HasSelection))]
    private void OpenPrompt()
    {
        var node = SelectedRow!.Node;
        string dir = node.IsDirectory ? node.Path : Path.GetDirectoryName(node.Path) ?? node.Path;
        Try(() => FileActions.OpenCommandPromptHere(dir));
    }

    [RelayCommand(CanExecute = nameof(HasSelection))]
    private void CopyPath() => Try(() => Clipboard.SetText(SelectedRow!.Node.Path));

    [RelayCommand(CanExecute = nameof(HasSelection))]
    private async Task RecycleAsync()
    {
        var node = SelectedRow!.Node;
        bool confirmed = ConfirmRequested?.Invoke("Send to Recycle Bin?",
            $"{node.Path}\n{Formatting.Bytes(node.Size)}", "Send to Recycle Bin") ?? false;
        if (!confirmed) return;

        bool ok = FileActions.SendToRecycleBin(node.Path);
        StatusText = ok ? $"Sent to Recycle Bin: {node.Name}" : $"Could not recycle: {node.Name}";
        if (ok) await RescanAsync();
    }

    [RelayCommand(CanExecute = nameof(HasSelection))]
    private async Task ArchiveAsync()
    {
        var node = SelectedRow!.Node;
        StatusText = $"Archiving {node.Name} ...";
        try
        {
            string zip = await Task.Run(() => FileActions.Archive(node.Path));
            StatusText = $"Archived to {Path.GetFileName(zip)}";
        }
        catch (Exception ex)
        {
            StatusText = $"Archive failed: {ex.Message}";
        }
    }

    private bool HasSelection() => SelectedRow is not null;

    // ---------------- Duplicates ----------------

    [RelayCommand(CanExecute = nameof(CanFindDuplicates))]
    private async Task FindDuplicatesAsync()
    {
        if (_scanRoot is null) return;
        IsFindingDuplicates = true;
        StatusText = "Scanning for duplicates ...";
        UpdateCommandStates();
        try
        {
            var root = _scanRoot;
            var progress = new Progress<DuplicateProgress>(p =>
                ProgressText = $"Hashing {p.FilesHashed:N0}/{p.TotalCandidates:N0}");
            var groups = await Task.Run(() => _duplicateFinder.Find(root, minimumSize: 1, progress));

            DuplicateGroups.Clear();
            long wasted = 0;
            foreach (var g in groups)
            {
                DuplicateGroups.Add(new DuplicateGroupViewModel(g));
                wasted += g.WastedBytes;
            }
            StatusText = groups.Count == 0
                ? "No duplicates found."
                : $"{groups.Count:N0} duplicate sets · {Formatting.Bytes(wasted)} reclaimable";
        }
        catch (Exception ex)
        {
            StatusText = $"Duplicate scan failed: {ex.Message}";
        }
        finally
        {
            IsFindingDuplicates = false;
            ProgressText = string.Empty;
            UpdateCommandStates();
        }
    }

    private bool CanFindDuplicates() => _scanRoot is not null && !IsFindingDuplicates && !IsScanning;

    [RelayCommand]
    private async Task RecycleDuplicatesAsync(DuplicateGroupViewModel group)
    {
        if (group is null || group.Paths.Count < 2) return;
        // Keep the first copy, recycle the rest.
        var toDelete = group.Paths.Skip(1).ToList();
        bool confirmed = ConfirmRequested?.Invoke("Send to Recycle Bin?",
            $"Keep:\n{group.Paths[0]}\n\nSend {toDelete.Count} duplicate copies to the Recycle Bin?", "Send to Recycle Bin") ?? false;
        if (!confirmed) return;

        bool ok = FileActions.SendToRecycleBin(toDelete);
        if (ok)
        {
            DuplicateGroups.Remove(group);
            StatusText = $"Recycled {toDelete.Count} duplicates.";
        }
        else
        {
            StatusText = "Could not recycle some duplicates.";
        }
        await Task.CompletedTask;
    }

    // ---------------- History ----------------

    [RelayCommand(CanExecute = nameof(CanRescan))]
    private void ShowHistory() => ShowHistoryRequested?.Invoke(_scanRoot?.Path);

    public SnapshotStore Store => _store.Value;

    // ---------------- Junk cleanup ----------------

    [RelayCommand]
    private async Task ShowCleanupAsync()
    {
        SelectedTabIndex = 2;
        if (CleanupCategories.Count == 0)
            await ScanForJunkAsync();
    }

    [RelayCommand(CanExecute = nameof(CanRunCleanup))]
    private async Task ScanForJunkAsync()
    {
        IsScanningCleanup = true;
        CleanupStatusText = "Scanning for junk files...";
        UpdateCommandStates();
        try
        {
            var results = await Task.Run(CleanupScanner.Scan);

            // Re-scanning (e.g. after cleaning) should preserve what the user had checked.
            var previousSelection = CleanupCategories.ToDictionary(c => c.Result.Kind, c => c.IsSelected);

            CleanupCategories.Clear();
            long total = 0;
            foreach (var r in results)
            {
                var vm = new CleanupCategoryViewModel(r);
                if (previousSelection.TryGetValue(r.Kind, out bool wasSelected)) vm.IsSelected = wasSelected;
                vm.PropertyChanged += OnCleanupCategoryPropertyChanged;
                CleanupCategories.Add(vm);
                total += r.TotalSize;
            }
            UpdateCleanupTotal();
            CleanupStatusText = total == 0
                ? "No junk found — everything's already clean."
                : $"Found {Formatting.Bytes(total)} reclaimable across {results.Count} categories.";
        }
        catch (Exception ex)
        {
            CleanupStatusText = $"Scan failed: {ex.Message}";
        }
        finally
        {
            IsScanningCleanup = false;
            UpdateCommandStates();
        }
    }

    private void OnCleanupCategoryPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(CleanupCategoryViewModel.IsSelected))
            UpdateCleanupTotal();
    }

    private void UpdateCleanupTotal()
    {
        long total = CleanupCategories.Where(c => c.IsSelected).Sum(c => c.TotalSize);
        CleanupTotalText = Formatting.Bytes(total);
        CleanSelectedCommand.NotifyCanExecuteChanged();
    }

    private bool CanRunCleanup() => !IsScanningCleanup && !IsCleaning;

    [RelayCommand(CanExecute = nameof(CanCleanSelected))]
    private async Task CleanSelectedAsync()
    {
        var selected = CleanupCategories.Where(c => c.IsSelected && c.TotalSize > 0).ToList();
        if (selected.Count == 0) return;

        string itemList = string.Join("\n", selected.Select(c => $"• {c.Name} — {c.SizeText}"));
        bool confirmed = ConfirmRequested?.Invoke("Clean Selected?",
            $"This will permanently delete:\n\n{itemList}\n\nTotal: {CleanupTotalText}", "Clean") ?? false;
        if (!confirmed) return;

        IsCleaning = true;
        CleanupStatusText = "Cleaning...";
        UpdateCommandStates();
        try
        {
            long freed = 0;
            int failedFiles = 0;
            foreach (var category in selected)
            {
                var result = await Task.Run(() => CleanupCleaner.Clean(category.Result));
                freed += result.BytesFreed;
                failedFiles += result.FailedCount;
            }
            CleanupStatusText = failedFiles == 0
                ? $"Freed {Formatting.Bytes(freed)}."
                : $"Freed {Formatting.Bytes(freed)} ({failedFiles:N0} files skipped — in use).";
        }
        catch (Exception ex)
        {
            CleanupStatusText = $"Cleanup failed: {ex.Message}";
        }
        finally
        {
            IsCleaning = false;
            UpdateCommandStates();
        }

        await ScanForJunkAsync(); // refresh sizes to reflect what was actually freed
    }

    private bool CanCleanSelected() =>
        !IsScanningCleanup && !IsCleaning && CleanupCategories.Any(c => c.IsSelected && c.TotalSize > 0);

    // ---------------- helpers ----------------

    private void Try(Action action)
    {
        try { action(); }
        catch (Exception ex) { StatusText = ex.Message; }
    }

    private void UpdateCommandStates()
    {
        RescanCommand.NotifyCanExecuteChanged();
        CancelCommand.NotifyCanExecuteChanged();
        ShowHistoryCommand.NotifyCanExecuteChanged();
        FindDuplicatesCommand.NotifyCanExecuteChanged();
        RevealCommand.NotifyCanExecuteChanged();
        OpenCommand.NotifyCanExecuteChanged();
        OpenPromptCommand.NotifyCanExecuteChanged();
        CopyPathCommand.NotifyCanExecuteChanged();
        RecycleCommand.NotifyCanExecuteChanged();
        ArchiveCommand.NotifyCanExecuteChanged();
        ScanForJunkCommand.NotifyCanExecuteChanged();
        CleanSelectedCommand.NotifyCanExecuteChanged();
    }
}
