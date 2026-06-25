using DiskMap.App.Infrastructure;
using DiskMap.Core.Duplicates;

namespace DiskMap.App.ViewModels;

public sealed class DuplicateGroupViewModel
{
    public DuplicateGroupViewModel(DuplicateGroup group)
    {
        Group = group;
    }

    public DuplicateGroup Group { get; }
    public long Size => Group.Size;
    public int Count => Group.Paths.Count;
    public long WastedBytes => Group.WastedBytes;
    public IReadOnlyList<string> Paths => Group.Paths;

    public string Header =>
        $"{Count} copies · {Formatting.Bytes(Size)} each · {Formatting.Bytes(WastedBytes)} reclaimable";
}
