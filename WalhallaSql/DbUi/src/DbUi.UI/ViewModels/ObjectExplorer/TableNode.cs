using System.Globalization;
using CommunityToolkit.Mvvm.Input;
using DbUi.Core.Catalog;
using DbUi.UI.Services;

namespace DbUi.UI.ViewModels.ObjectExplorer;

public sealed partial class TableNode : TreeNodeViewModel
{
    private readonly CatalogNode _node;
    private readonly ICatalogBrowser _catalog;
    private readonly Action<string> _insertQuery;
    private readonly IDialogService _dialogService;

    public TableNode(CatalogNode node, ICatalogBrowser catalog, Action<string> insertQuery, IDialogService dialogService)
        : base(node.DisplayName, hasChildren: node.HasChildren)
    {
        _node = node;
        _catalog = catalog;
        _insertQuery = insertQuery;
        _dialogService = dialogService;
    }

    protected override async Task<IEnumerable<TreeNodeViewModel>> LoadChildrenAsync()
    {
        var children = await _catalog.GetChildrenAsync(_node.Id);
        return children.Select(child => CatalogTreeNodeFactory.Create(child, _catalog, _insertQuery, _dialogService));
    }

    private string? GetActionCommandText(string actionId) =>
        _node.Actions?.FirstOrDefault(action => string.Equals(action.ActionId, actionId, StringComparison.OrdinalIgnoreCase))?.CommandText;

    private string? GetTableName() =>
        _node.Metadata?.TryGetValue("objectName", out var value) == true ? value : null;

    private IReadOnlyList<string> GetColumns() =>
        _node.Metadata?.TryGetValue("columns", out var value) == true && !string.IsNullOrWhiteSpace(value)
            ? value.Split(',', StringSplitOptions.RemoveEmptyEntries)
            : Array.Empty<string>();

    [RelayCommand]
    private void ScriptSelectTop()
    {
        var sql = GetActionCommandText("select-top");
        if (!string.IsNullOrEmpty(sql))
            _insertQuery(sql);
    }

    [RelayCommand]
    private void ScriptSelectAll()
    {
        var sql = GetActionCommandText("select-all");
        if (!string.IsNullOrEmpty(sql))
            _insertQuery(sql);
    }

    [RelayCommand]
    private void ScriptCount()
    {
        var sql = GetActionCommandText("count");
        if (!string.IsNullOrEmpty(sql))
            _insertQuery(sql);
    }

    [RelayCommand]
    private void NewIndex()
    {
        var tableName = GetTableName();
        if (string.IsNullOrWhiteSpace(tableName)) return;

        var sql = _dialogService.ShowCreateIndexDialog(tableName, GetColumns());
        if (!string.IsNullOrEmpty(sql))
            _insertQuery(sql);
    }

    [RelayCommand]
    private void NewTrigger()
    {
        var tableName = GetTableName();
        if (string.IsNullOrWhiteSpace(tableName)) return;

        var sql = _dialogService.ShowCreateTriggerDialog(tableName);
        if (!string.IsNullOrEmpty(sql))
            _insertQuery(sql);
    }
}
