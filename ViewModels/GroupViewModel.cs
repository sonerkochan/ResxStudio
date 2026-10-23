namespace ResxStudio.ViewModels;

/// <summary>A section in the sidebar: the key prefix before the first dot.</summary>
public sealed class GroupViewModel : ObservableObject
{
    private int _count;
    private int _missingCount;

    public GroupViewModel(string name, bool isAll)
    {
        Name = name;
        IsAll = isAll;
    }

    public string Name { get; }
    public bool IsAll { get; }
    public string DisplayName => IsAll ? "All keys" : Name;

    public int Count => _count;
    public int MissingCount => _missingCount;
    public bool HasMissing => _missingCount > 0;

    public void Update(int count, int missingCount)
    {
        if (_count != count)
        {
            _count = count;
            OnPropertyChanged(nameof(Count));
        }
        if (_missingCount != missingCount)
        {
            _missingCount = missingCount;
            OnPropertyChanged(nameof(MissingCount));
            OnPropertyChanged(nameof(HasMissing));
        }
    }
}
