using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using DiskMap.App.Infrastructure;
using DiskMap.App.Treemap;
using DiskMap.Core.Scanning;

namespace DiskMap.App.Controls;

/// <summary>
/// Radial ("sunburst") view of the same scan data the treemap shows. Each ring is one depth
/// level; arc width is proportional to bytes. The center hub is the current root — clicking it
/// drills up if there's a parent. Same color scheme, same select/drill events, as
/// <see cref="TreemapControl"/> so switching tabs doesn't require relearning anything.
/// </summary>
public sealed class SunburstControl : FrameworkElement
{
    private readonly SunburstLayout _layout = new();
    private bool _layoutDirty = true;
    private static readonly Pen ThinBorder = CreatePen(Color.FromArgb(0x40, 0, 0, 0), 0.5);
    private static readonly Pen SelectionPen = CreatePen(Color.FromRgb(0xFF, 0xFF, 0xFF), 2.0);
    private static readonly Brush HubBrush = Freeze(new SolidColorBrush(Color.FromRgb(0x33, 0x35, 0x3D)));

    public static readonly DependencyProperty RootNodeProperty = DependencyProperty.Register(
        nameof(RootNode), typeof(FileSystemNode), typeof(SunburstControl),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender, OnRootChanged));

    public static readonly DependencyProperty SelectedNodeProperty = DependencyProperty.Register(
        nameof(SelectedNode), typeof(FileSystemNode), typeof(SunburstControl),
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

    public SunburstControl()
    {
        ClipToBounds = true;
        Focusable = true;
    }

    private static void OnRootChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var control = (SunburstControl)d;
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
        {
            var center = new Point(ActualWidth / 2, ActualHeight / 2);
            double maxRadius = Math.Max(1, Math.Min(ActualWidth, ActualHeight) / 2 - 4);
            _layout.Build(RootNode, center, maxRadius);
        }
        _layoutDirty = false;
    }

    protected override void OnRender(DrawingContext dc)
    {
        if (RootNode is null) return;
        EnsureLayout();

        foreach (var seg in _layout.Segments)
        {
            if (seg.SweepAngle <= 0) continue;
            Brush brush = seg.Node.IsDirectory ? FileTypeColors.DirectoryBrush : FileTypeColors.GetBrush(seg.Node.Extension);
            var geometry = BuildWedge(_layout.Center, seg.InnerRadius, seg.OuterRadius, seg.StartAngle, seg.SweepAngle);
            dc.DrawGeometry(brush, ThinBorder, geometry);
        }

        // Center hub: current root, clickable to drill up.
        dc.DrawEllipse(HubBrush, ThinBorder, _layout.Center, _layout.HubRadius, _layout.HubRadius);
        var typeface = new Typeface("Segoe UI");
        var label = new FormattedText(TrimForHub(RootNode.Name), System.Globalization.CultureInfo.CurrentCulture,
            FlowDirection.LeftToRight, typeface, 11, Brushes.White, VisualTreeHelper.GetDpi(this).PixelsPerDip);
        label.MaxTextWidth = Math.Max(1, _layout.HubRadius * 1.8);
        label.TextAlignment = TextAlignment.Center;
        dc.DrawText(label, new Point(_layout.Center.X - label.Width / 2, _layout.Center.Y - label.Height / 2));

        // Selection highlight.
        if (SelectedNode is not null)
        {
            foreach (var seg in _layout.Segments)
            {
                if (!ReferenceEquals(seg.Node, SelectedNode)) continue;
                var geometry = BuildWedge(_layout.Center, seg.InnerRadius, seg.OuterRadius, seg.StartAngle, seg.SweepAngle);
                dc.DrawGeometry(null, SelectionPen, geometry);
                break;
            }
        }
    }

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonDown(e);
        Focus();
        EnsureLayout();
        if (RootNode is null) return;
        if (_layout.TryHitTest(e.GetPosition(this), RootNode, out var node))
        {
            if (ReferenceEquals(node, RootNode))
            {
                if (e.ClickCount == 2 && RootNode.Parent is not null)
                    DrillRequested?.Invoke(this, RootNode.Parent);
                return;
            }
            SelectedNode = node;
            if (e.ClickCount == 2 && node.IsDirectory)
                DrillRequested?.Invoke(this, node);
        }
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        EnsureLayout();
        if (RootNode is not null && _layout.TryHitTest(e.GetPosition(this), RootNode, out var node))
        {
            string text = $"{node.Path}\n{Formatting.Bytes(node.Size)}";
            if (ToolTip as string != text) ToolTip = text;
        }
        else
        {
            ToolTip = null;
        }
    }

    private static string TrimForHub(string name) => name.Length > 14 ? name[..12] + "…" : name;

    private static Geometry BuildWedge(Point center, double innerRadius, double outerRadius, double startDegrees, double sweepDegrees)
    {
        if (sweepDegrees >= 359.99) sweepDegrees = 359.99; // ArcSegment can't represent a closed full circle
        double startRad = startDegrees * Math.PI / 180.0;
        double endRad = (startDegrees + sweepDegrees) * Math.PI / 180.0;
        bool largeArc = sweepDegrees > 180;

        Point OuterPoint(double rad) => new(center.X + outerRadius * Math.Cos(rad), center.Y + outerRadius * Math.Sin(rad));
        Point InnerPoint(double rad) => new(center.X + innerRadius * Math.Cos(rad), center.Y + innerRadius * Math.Sin(rad));

        var figure = new PathFigure { StartPoint = InnerPoint(startRad), IsClosed = true };
        figure.Segments.Add(new LineSegment(OuterPoint(startRad), true));
        figure.Segments.Add(new ArcSegment(OuterPoint(endRad), new Size(outerRadius, outerRadius), 0, largeArc, SweepDirection.Clockwise, true));
        figure.Segments.Add(new LineSegment(InnerPoint(endRad), true));
        if (innerRadius > 0.01)
            figure.Segments.Add(new ArcSegment(InnerPoint(startRad), new Size(innerRadius, innerRadius), 0, largeArc, SweepDirection.Counterclockwise, true));

        var geometry = new PathGeometry();
        geometry.Figures.Add(figure);
        return geometry;
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
