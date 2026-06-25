using System.Windows.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using DiskMap.App.Infrastructure;
using DiskMap.Core.FileTypes;

namespace DiskMap.App.ViewModels;

public sealed partial class ExtensionStatViewModel : ObservableObject
{
    private readonly MainViewModel? _owner;

    public ExtensionStatViewModel(ExtensionStat stat, long totalSize, MainViewModel? owner = null)
    {
        Extension = stat.Extension;
        TotalSize = stat.TotalSize;
        FileCount = stat.FileCount;
        Percentage = totalSize > 0 ? (double)stat.TotalSize / totalSize * 100.0 : 0;
        Brush = FileTypeColors.GetBrush(stat.Extension);
        _owner = owner;
    }

    public string Extension { get; }
    public long TotalSize { get; }
    public long FileCount { get; }
    public double Percentage { get; }
    public Brush Brush { get; }
    public string SizeText => Formatting.Bytes(TotalSize);
    public string PercentText => $"{Percentage:N1}%";

    [ObservableProperty]
    private bool _isSelected;

    partial void OnIsSelectedChanged(bool value) => _owner?.OnFiltersChanged();
}
