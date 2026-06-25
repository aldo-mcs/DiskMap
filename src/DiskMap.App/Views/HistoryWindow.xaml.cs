using System.Windows;
using System.Windows.Media;
using DiskMap.App.Infrastructure;
using DiskMap.Core.Snapshots;

namespace DiskMap.App.Views;

public partial class HistoryWindow : Window
{
    private readonly SnapshotStore _store;

    private void OnClose(object sender, RoutedEventArgs e) => Close();

    private sealed record SnapshotChoice(SnapshotInfo Info, string Display);

    private sealed class DeltaRow
    {
        public required string Path { get; init; }
        public required string OldText { get; init; }
        public required string NewText { get; init; }
        public required string ChangeText { get; init; }
        public required Brush ChangeBrush { get; init; }
    }

    public HistoryWindow(SnapshotStore store, string? root)
    {
        InitializeComponent();
        WindowChromeHelper.ApplyDwmPolish(this);
        _store = store;

        var snapshots = _store.List(root);
        var choices = snapshots
            .Select(s => new SnapshotChoice(s, $"{s.TakenUtc.ToLocalTime():yyyy-MM-dd HH:mm}  ·  {Formatting.Bytes(s.TotalSize)}  ·  {s.TotalFiles:N0} files"))
            .ToList();

        BaselineCombo.ItemsSource = choices;
        CompareCombo.ItemsSource = choices;

        if (choices.Count >= 2)
        {
            CompareCombo.SelectedIndex = 0;   // newest
            BaselineCombo.SelectedIndex = 1;  // previous
        }
        else if (choices.Count == 1)
        {
            CompareCombo.SelectedIndex = 0;
            SummaryText.Text = "Only one snapshot exists for this folder. Scan again later to compare.";
        }
        else
        {
            SummaryText.Text = "No snapshots yet. Scan this folder to record one.";
        }
    }

    private void OnSelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (BaselineCombo.SelectedItem is not SnapshotChoice baseline ||
            CompareCombo.SelectedItem is not SnapshotChoice compare)
            return;

        if (baseline.Info.Id == compare.Info.Id)
        {
            SummaryText.Text = "Select two different snapshots.";
            DiffList.ItemsSource = null;
            return;
        }

        var older = _store.LoadEntrySizes(baseline.Info.Id);
        var newer = _store.LoadEntrySizes(compare.Info.Id);
        var deltas = SnapshotDiff.Compare(older, newer);

        long totalDelta = compare.Info.TotalSize - baseline.Info.TotalSize;
        SummaryText.Text = $"Total change: {Formatting.SignedBytes(totalDelta)}  ({deltas.Count} directories changed)";

        DiffList.ItemsSource = deltas.Select(d => new DeltaRow
        {
            Path = d.Path,
            OldText = Formatting.Bytes(d.OldSize),
            NewText = Formatting.Bytes(d.NewSize),
            ChangeText = Formatting.SignedBytes(d.Delta),
            ChangeBrush = (Brush)(d.Delta >= 0
                ? Application.Current.Resources["CoralBrush"]
                : Application.Current.Resources["ShrinkBrush"]),
        }).ToList();
    }
}
