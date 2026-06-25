using System.Windows.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using DiskMap.App.Infrastructure;
using DiskMap.Core.Scanning;

namespace DiskMap.App.ViewModels;

/// <summary>A single row in the flattened tree-table. Wraps one <see cref="FileSystemNode"/>.</summary>
public sealed partial class NodeViewModel : ObservableObject
{
    private readonly MainViewModel _owner;

    public NodeViewModel(FileSystemNode node, MainViewModel owner, int depth)
    {
        Node = node;
        _owner = owner;
        Depth = depth;
    }

    public FileSystemNode Node { get; }
    public int Depth { get; }

    public bool HasChildren => Node.IsDirectory && Node.Children.Count > 0;
    public string Name => Node.Name;
    public bool IsDirectory => Node.IsDirectory;
    public long Size => Node.Size;
    public long Allocated => Node.AllocatedSize;
    public long Files => Node.FileCount;
    public DateTime LastModified => Node.LastModified == DateTime.MinValue ? DateTime.MinValue : Node.LastModified.ToLocalTime();
    public DateTime Created => Node.Created == DateTime.MinValue ? DateTime.MinValue : Node.Created.ToLocalTime();
    public string AttributesText => Formatting.FileAttributesText(Node.Attributes);
    public long Slack => Node.AllocatedSize - Node.Size;
    public double Percentage => Node.FractionOfParent * 100.0;

    public string MftIndexText => Node.MftRecordIndex is { } i ? i.ToString() : "—";
    public string HardLinksText => Node.HardLinkCount > 0 ? Node.HardLinkCount.ToString() : "—";
    public string StreamsText => Node.AlternateStreamCount > 0 ? Node.AlternateStreamCount.ToString() : "—";
    public bool IsReparsePoint => Node.IsReparsePoint;

    public Brush TypeBrush => Node.IsDirectory ? FileTypeColors.DirectoryBrush : FileTypeColors.GetBrush(Node.Extension);
    public string Glyph => Node.IsDirectory ? "" : ""; // Segoe MDL2: folder / document

    [ObservableProperty]
    private bool _isExpanded;

    partial void OnIsExpandedChanged(bool value) => _owner.OnRowExpandChanged(this, value);
}
