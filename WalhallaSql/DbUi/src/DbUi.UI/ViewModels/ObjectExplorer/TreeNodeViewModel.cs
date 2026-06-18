using CommunityToolkit.Mvvm.ComponentModel;
using System.Collections.ObjectModel;

namespace DbUi.UI.ViewModels.ObjectExplorer;

public abstract class TreeNodeViewModel : ObservableObject
{
    private bool _isExpanded;
    private bool _isLoaded;

    protected TreeNodeViewModel(string displayName, bool hasChildren)
    {
        DisplayName = displayName;
        if (hasChildren)
            Children.Add(LoadingNode.Instance);
    }

    public string DisplayName { get; }
    public ObservableCollection<TreeNodeViewModel> Children { get; } = [];

    public bool IsExpanded
    {
        get => _isExpanded;
        set
        {
            if (SetProperty(ref _isExpanded, value) && value && !_isLoaded)
                _ = ExpandAsync();
        }
    }

    public bool IsSelected { get => _isSelected; set => SetProperty(ref _isSelected, value); }
    private bool _isSelected;

    private async Task ExpandAsync()
    {
        _isLoaded = true;
        Children.Clear();
        try
        {
            var children = await LoadChildrenAsync();
            foreach (var child in children)
                Children.Add(child);
            if (!Children.Any())
                Children.Add(new EmptyNode());
        }
        catch (Exception ex)
        {
            Children.Clear();
            Children.Add(new ErrorNode(ex.Message));
        }
    }

    protected abstract Task<IEnumerable<TreeNodeViewModel>> LoadChildrenAsync();
}

public sealed class LoadingNode : TreeNodeViewModel
{
    public static readonly LoadingNode Instance = new();
    private LoadingNode() : base("Loading…", hasChildren: false) { }
    protected override Task<IEnumerable<TreeNodeViewModel>> LoadChildrenAsync() =>
        Task.FromResult(Enumerable.Empty<TreeNodeViewModel>());
}

public sealed class EmptyNode : TreeNodeViewModel
{
    public EmptyNode() : base("(empty)", hasChildren: false) { }
    protected override Task<IEnumerable<TreeNodeViewModel>> LoadChildrenAsync() =>
        Task.FromResult(Enumerable.Empty<TreeNodeViewModel>());
}

public sealed class ErrorNode : TreeNodeViewModel
{
    public ErrorNode(string message) : base($"Error: {message}", hasChildren: false) { }
    protected override Task<IEnumerable<TreeNodeViewModel>> LoadChildrenAsync() =>
        Task.FromResult(Enumerable.Empty<TreeNodeViewModel>());
}
