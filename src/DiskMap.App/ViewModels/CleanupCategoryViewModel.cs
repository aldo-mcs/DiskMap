using CommunityToolkit.Mvvm.ComponentModel;
using DiskMap.App.Infrastructure;
using DiskMap.Core.Cleanup;

namespace DiskMap.App.ViewModels;

public sealed partial class CleanupCategoryViewModel : ObservableObject
{
    public CleanupCategoryViewModel(CleanupCategoryResult result)
    {
        Result = result;
        // Recycle Bin emptying is irreversible by nature (unlike regenerable temp/cache junk),
        // so it starts unchecked — everything else starts pre-selected, like most cleaners.
        _isSelected = result.Kind != CleanupCategoryKind.RecycleBin;
    }

    public CleanupCategoryResult Result { get; private set; }

    [ObservableProperty]
    private bool _isSelected;

    public string Name => Result.Name;
    public string Description => Result.Description;
    public long TotalSize => Result.TotalSize;
    public string SizeText => Formatting.Bytes(Result.TotalSize);
    public string FileCountText => $"{Result.FileCount:N0} items";
    public bool IsEmpty => Result.TotalSize == 0;

    public void UpdateResult(CleanupCategoryResult result)
    {
        Result = result;
        OnPropertyChanged(nameof(Name));
        OnPropertyChanged(nameof(Description));
        OnPropertyChanged(nameof(TotalSize));
        OnPropertyChanged(nameof(SizeText));
        OnPropertyChanged(nameof(FileCountText));
        OnPropertyChanged(nameof(IsEmpty));
    }
}
