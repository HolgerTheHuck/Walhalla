namespace DbUi.UI.ViewModels.ObjectExplorer;

public sealed class FolderNode : TreeNodeViewModel
{
    private readonly Func<Task<IEnumerable<TreeNodeViewModel>>> _loader;

    public FolderNode(string name, Func<Task<IEnumerable<TreeNodeViewModel>>> loader)
        : base(name, hasChildren: true)
    {
        _loader = loader;
    }

    protected override Task<IEnumerable<TreeNodeViewModel>> LoadChildrenAsync() => _loader();
}
