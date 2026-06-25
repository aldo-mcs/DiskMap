using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using DiskMap.App.Infrastructure;
using DiskMap.App.Treemap;
using DiskMap.Core.Scanning;

namespace DiskMap.App.Controls;

/// <summary>
/// Custom-rendered squarified treemap. Files are colored by extension; clicking selects a
/// node (two-way bound to <see cref="SelectedNode"/>); double-clicking a directory raises
/// <see cref="DrillRequested"/>. The layout is cached and only rebuilt on data/size change.
/// </summary>
public sealed class TreemapControl : FrameworkElement
{
    private readonly TreemapLayout _layout = new();
    private bool _layoutDirty = true;
    private static readonly Pen ThinBorder = CreatePen(Color.FromArgb(0x40, 0, 0, 0), 0.5);
    private static readonly Pen SelectionPen = CreatePen(Color.FromRgb(0xFF, 0xFF, 0xFF), 2.0);
    private static readonly Brush EmptyBrush = Freeze(new SolidColorBrush(FileTypeColors.EmptyColor));

    public static readonly DependencyProperty RootNodeProperty = DependencyProperty.Register(
        nameof(RootNode), typeof(FileSystemNode), typeof(TreemapControl),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender, OnRootChanged));

    public static readonly DependencyProperty SelectedNodeProperty = DependencyProperty.Register(
        nameof(SelectedNode), typeof(FileSystemNode), typeof(TreemapControl),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.BindsTwoWayByDefault | FrameworkPropertyMetadataOptions.AffectsRender));

    public FileSystemNode? RootNode
    {
        get => (FileSystemNode?)GetValue(RootNodeProperty);
        set => SetValue(RootNodeProperty, value);
    }

    public FileSystemNode? SelectedNode
    {
        get => (FileSystemNode?)GetValue(SelectedNodeProperty);
        set => SetValue(SelectedNodeProperty, value);
    }

    public event EventHandler<FileSystemNode>? DrillRequested;

    public TreemapControl()
    {
        ClipToBounds = true;
        Focusable = true;
    }

    private static void OnRootChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var control = (TreemapControl)d;
        control._layoutDirty = true;
        control.InvalidateVisual();
    }

    protected override void OnRenderSizeChanged(SizeChangedInfo sizeInfo)
    {
        base.OnRenderSizeChanged(sizeInfo);
        _layoutDirty = true;
    }

    private void EnsureLayout()
    {
        if (!_layoutDirty) return;
        if (RootNode is not null && ActualWidth >= 1 && ActualHeight >= 1)
            _layout.Build(RootNode, new Rect(0, 0, ActualWidth, ActualHeight));
        _layoutDirty = false;
    }

    protected override void OnRender(DrawingContext dc)
    {
        dc.DrawRectangle(EmptyBrush, null, new Rect(0, 0, ActualWidth, ActualHeight));
        if (RootNode is null) return;
        EnsureLayout();

        foreach (var tile in _layout.Tiles)
        {
            if (tile.Rect.Width <= 0 || tile.Rect.Height <= 0) continue;
            Brush brush = tile.Node.IsDirectory
                ? FileTypeColors.DirectoryBrush
                : FileTypeColors.GetBrush(tile.Node.Extension);
            bool drawBorder = tile.Rect.Width > 4 && tile.Rect.Height > 4;
            dc.DrawRectangle(brush, drawBorder ? ThinBorder : null, tile.Rect);
        }

        // Selection highlight: outline the selected node's region if it is within view.
        if (SelectedNode is not null && _layout.Rects.TryGetValue(SelectedNode, out var sel) &&
            sel.Width > 0 && sel.Height > 0)
        {
            var r = new Rect(sel.X + 1, sel.Y + 1, Math.Max(0, sel.Width - 2), Math.Max(0, sel.Height - 2));
            dc.DrawRectangle(null, SelectionPen, r);
        }
    }

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonDown(e);
        Focus();
        EnsureLayout();
        if (_layout.TryHitTest(e.GetPosition(this), out var node))
        {
            SelectedNode = node;
            if (e.ClickCount == 2 && node.IsDirectory)
                DrillRequested?.Invoke(this, node);
        }
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        EnsureLayout();
        if (_layout.TryHitTest(e.GetPosition(this), out var node))
        {
            string text = $"{node.Path}\n{Formatting.Bytes(node.Size)}";
            if (ToolTip as string != text)
            {
                ToolTip = text;
            }
        }
        else
        {
            ToolTip = null;
        }
    }

    private static Pen CreatePen(Color color, double thickness)
    {
        var pen = new Pen(new SolidColorBrush(color), thickness);
        pen.Freeze();
        return pen;
    }

    private static Brush Freeze(Brush brush)
    {
        brush.Freeze();
        return brush;
    }
}
