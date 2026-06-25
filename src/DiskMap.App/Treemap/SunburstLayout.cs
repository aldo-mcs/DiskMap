using System.Windows;
using DiskMap.Core.Scanning;

namespace DiskMap.App.Treemap;

public readonly record struct SunburstSegment(FileSystemNode Node, double StartAngle, double SweepAngle, double InnerRadius, double OuterRadius, int Depth);

/// <summary>
/// Computes a radial ("sunburst") layout: each ring is one tree depth, divided into arcs sized
/// proportionally to each child's bytes, same size-weighting as <see cref="TreemapLayout"/> but
/// arranged by depth instead of nested rectangles — better at showing "how deep does this go"
/// at a glance, which a treemap's flat tiling does not convey.
/// </summary>
public sealed class SunburstLayout
{
    private const int MaxDepth = 6;
    private const double MinSweepDegrees = 0.5;

    private readonly List<SunburstSegment> _segments = [];
    public IReadOnlyList<SunburstSegment> Segments => _segments;

    public Point Center { get; private set; }
    public double RingThickness { get; private set; }
    public double HubRadius { get; private set; }

    public void Build(FileSystemNode root, Point center, double maxRadius)
    {
        _segments.Clear();
        if (maxRadius < 1) return;
        Center = center;
        RingThickness = maxRadius / (MaxDepth + 1);
        HubRadius = RingThickness * 0.9;
        Layout(root, 0, 360.0, 0, 0);
    }

    private void Layout(FileSystemNode node, double startAngle, double sweepAngle, int depth, double innerRadius)
    {
        double outerRadius = innerRadius + RingThickness;
        if (depth > 0) // depth 0 (the root) is drawn as the center hub, not a ring segment
            _segments.Add(new SunburstSegment(node, startAngle, sweepAngle, innerRadius, outerRadius, depth));

        if (depth >= MaxDepth || !node.IsDirectory || node.Children.Count == 0) return;

        double total = 0;
        foreach (var c in node.Children)
            if (c.Size > 0) total += c.Size;
        if (total <= 0) return;

        double angle = startAngle;
        foreach (var child in node.Children)
        {
            if (child.Size <= 0) continue;
            double childSweep = sweepAngle * (child.Size / total);
            if (childSweep >= MinSweepDegrees)
                Layout(child, angle, childSweep, depth + 1, outerRadius);
            angle += childSweep;
        }
    }

    /// <summary>True with the root node when the hub (center circle) is hit.</summary>
    public bool TryHitTest(Point p, FileSystemNode root, out FileSystemNode node)
    {
        double dx = p.X - Center.X, dy = p.Y - Center.Y;
        double radius = Math.Sqrt(dx * dx + dy * dy);

        if (radius <= HubRadius)
        {
            node = root;
            return true;
        }

        double angle = Math.Atan2(dy, dx) * 180.0 / Math.PI;
        if (angle < 0) angle += 360;

        foreach (var seg in _segments)
        {
            if (radius >= seg.InnerRadius && radius < seg.OuterRadius && IsAngleInRange(angle, seg.StartAngle, seg.SweepAngle))
            {
                node = seg.Node;
                return true;
            }
        }
        node = null!;
        return false;
    }

    private static bool IsAngleInRange(double angle, double start, double sweep)
    {
        double end = start + sweep;
        if (end <= 360) return angle >= start && angle < end;
        return angle >= start || angle < end - 360;
    }
}
