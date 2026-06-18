namespace DbUi.UI.ViewModels.ObjectExplorer;

public sealed class ProcedureNode : TreeNodeViewModel
{
    public ProcedureNode(string displayName)
        : base(displayName, hasChildren: false) { }

    protected override Task<IEnumerable<TreeNodeViewModel>> LoadChildrenAsync() =>
        Task.FromResult(Enumerable.Empty<TreeNodeViewModel>());
}
