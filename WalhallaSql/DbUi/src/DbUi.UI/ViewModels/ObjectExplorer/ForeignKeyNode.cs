using DbUi.Core.Catalog;

namespace DbUi.UI.ViewModels.ObjectExplorer;

public sealed class ForeignKeyNode : TreeNodeViewModel
{
    public ForeignKeyNode(string displayName)
        : base(displayName, hasChildren: false) { }

    protected override Task<IEnumerable<TreeNodeViewModel>> LoadChildrenAsync() =>
        Task.FromResult(Enumerable.Empty<TreeNodeViewModel>());
}
