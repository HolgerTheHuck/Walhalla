using CommunityToolkit.Mvvm.Input;
using DbUi.Core.Catalog;

namespace DbUi.UI.ViewModels.ObjectExplorer;

public sealed partial class StoredProcedureNode : TreeNodeViewModel
{
    private readonly CatalogNode _node;
    private readonly Action<string> _insertQuery;

    public StoredProcedureNode(CatalogNode node, Action<string> insertQuery)
        : base(node.DisplayName, hasChildren: false)
    {
        _node = node;
        _insertQuery = insertQuery;
    }

    protected override Task<IEnumerable<TreeNodeViewModel>> LoadChildrenAsync() =>
        Task.FromResult(Enumerable.Empty<TreeNodeViewModel>());

    private string? GetActionCommandText(string actionId) =>
        _node.Actions?.FirstOrDefault(action => string.Equals(action.ActionId, actionId, StringComparison.OrdinalIgnoreCase))?.CommandText;

    [RelayCommand]
    private void ScriptExec()
    {
        var sql = GetActionCommandText("exec");
        if (!string.IsNullOrEmpty(sql))
            _insertQuery(sql);
    }

    [RelayCommand]
    private void ScriptEdit()
    {
        var sql = GetActionCommandText("edit");
        if (!string.IsNullOrEmpty(sql))
            _insertQuery(sql);
    }

    [RelayCommand]
    private void ScriptDrop()
    {
        var sql = GetActionCommandText("drop");
        if (!string.IsNullOrEmpty(sql))
            _insertQuery(sql);
    }
}
