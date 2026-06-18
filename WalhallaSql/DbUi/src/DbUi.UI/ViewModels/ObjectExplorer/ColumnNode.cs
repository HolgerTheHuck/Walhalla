namespace DbUi.UI.ViewModels.ObjectExplorer;

public sealed class ColumnNode : TreeNodeViewModel
{
    public ColumnNode(string displayText)
        : base(displayText, hasChildren: false) { }

    protected override Task<IEnumerable<TreeNodeViewModel>> LoadChildrenAsync() =>
        Task.FromResult(Enumerable.Empty<TreeNodeViewModel>());
}
