using System.Windows;
using DiskMap.Core.Scanning;

namespace DiskMap.App.Treemap;

public readonly record struct TreemapTile(Rect Rect, FileSystemNode Node);

/// <summary>
/// Computes a squarified treemap layout for a directory tree. Produces leaf tiles (one per
/// rendered file or per directory that is too small to descend into) plus a node->rect map
/// used for selection highlighting. Pruning small rectangles keeps tile count bounded
/// regardless of how many files were scanned.
/// </summary>
public sealed class TreemapLayout
{
    private const double MinSide = 3.0;
    private const int MaxDepth = 18;

    private readonly List<TreemapTile> _tiles = [];
    private readonly Dictionary<FileSystemNode, Rect> _rects = new();

    public IReadOnlyList<TreemapTile> Tiles => _tiles;
    public IReadOnlyDictionary<FileSystemNode, Rect> Rects => _rects;

    public void Build(FileSystemNode root, Rect bounds)
    {
        _tiles.Clear();
        _rects.Clear();
        if (bounds.Width < 1 || bounds.Height < 1) return;
        _rects[root] = bounds;
        Layout(root, bounds, 0);
    }

    public bool TryHitTest(Point p, out FileSystemNode node)
    {
        // Leaf tiles are non-overlapping; first containing tile wins.
        foreach (var tile in _tiles)
        {
            if (tile.Rect.Contains(p))
            {
                node = tile.Node;
                return true;
            }
        }
        node = null!;
        return false;
    }

    private void Layout(FileSystemNode node, Rect rect, int depth)
    {
        if (rect.Width < MinSide || rect.Height < MinSide || depth >= MaxDepth ||
            !node.IsDirectory || node.Children.Count == 0)
        {
            _tiles.Add(new TreemapTile(rect, node));
            return;
        }

        foreach (var (child, childRect) in Squarify(node.Children, rect))
        {
            _rects[child] = childRect;
            if (child.IsDirectory)
                Layout(child, childRect, depth + 1);
            else
                _tiles.Add(new TreemapTile(childRect, child));
        }
    }

    private static List<(FileSystemNode node, Rect rect)> Squarify(IReadOnlyList<FileSystemNode> children, Rect bounds)
    {
        var result = new List<(FileSystemNode, Rect)>();
        // children arrive already sorted by size desc from the scanner; filter out zero-size.
        var nodes = new List<FileSystemNode>(children.Count);
        double totalSize = 0;
        foreach (var c in children)
        {
            if (c.Size > 0) { nodes.Add(c); totalSize += c.Size; }
        }
        if (nodes.Count == 0 || totalSize <= 0) return result;

        double scale = bounds.Width * bounds.Height / totalSize;
        var areas = new double[nodes.Count];
        for (int i = 0; i < nodes.Count; i++) areas[i] = nodes[i].Size * scale;

        var rect = bounds;
        int start = 0;
        while (start < nodes.Count)
        {
            double shortSide = Math.Min(rect.Width, rect.Height);
            if (shortSide <= 0) break;

            int count = 1;
            double rowArea = areas[start];
            double worst = Worst(areas, start, count, shortSide, rowArea);
            while (start + count < nodes.Count)
            {
                double newRowArea = rowArea + areas[start + count];
                double newWorst = Worst(areas, start, count + 1, shortSide, newRowArea);
                if (newWorst > worst) break;
                count++;
                rowArea = newRowArea;
                worst = newWorst;
            }

            double thickness = rowArea / shortSide;
            if (rect.Width >= rect.Height)
            {
                double y = rect.Y;
                for (int k = 0; k < count; k++)
                {
                    double h = thickness > 0 ? areas[start + k] / thickness : 0;
                    result.Add((nodes[start + k], new Rect(rect.X, y, thickness, h)));
                    y += h;
                }
                rect = new Rect(rect.X + thickness, rect.Y, Math.Max(0, rect.Width - thickness), rect.Height);
            }
            else
            {
                double x = rect.X;
                for (int k = 0; k < count; k++)
                {
                    double w = thickness > 0 ? areas[start + k] / thickness : 0;
                    result.Add((nodes[start + k], new Rect(x, rect.Y, w, thickness)));
                    x += w;
                }
                rect = new Rect(rect.X, rect.Y + thickness, rect.Width, Math.Max(0, rect.Height - thickness));
            }
            start += count;
        }
        return result;
    }

    private static double Worst(double[] areas, int start, int count, double shortSide, double rowArea)
    {
        double max = double.MinValue, min = double.MaxValue;
        for (int i = start; i < start + count; i++)
        {
            if (areas[i] > max) max = areas[i];
            if (areas[i] < min) min = areas[i];
        }
        if (rowArea <= 0 || min <= 0) return double.MaxValue;
        double s2 = rowArea * rowArea;
        double w2 = shortSide * shortSide;
        return Math.Max(w2 * max / s2, s2 / (w2 * min));
    }
}
