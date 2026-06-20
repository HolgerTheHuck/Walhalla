using CommunityToolkit.Mvvm.Input;
using DbUi.Core.Catalog;
using DbUi.UI.Services;

namespace DbUi.UI.ViewModels.ObjectExplorer;

public sealed partial class FolderNode : TreeNodeViewModel
{
    private readonly Func<Task<IEnumerable<TreeNodeViewModel>>> _loader;
    private readonly CatalogNode? _node;
    private readonly IDialogService? _dialogService;
    private readonly Action<string>? _insertQuery;

    public FolderNode(
        string name,
        Func<Task<IEnumerable<TreeNodeViewModel>>> loader,
        CatalogNode? node = null,
        IDialogService? dialogService = null,
        Action<string>? insertQuery = null)
        : base(name, hasChildren: true)
    {
        _loader = loader;
        _node = node;
        _dialogService = dialogService;
        _insertQuery = insertQuery;
    }

    public string? FolderKind => _node?.Metadata?.TryGetValue("folderKind", out var value) == true ? value : null;

    protected override Task<IEnumerable<TreeNodeViewModel>> LoadChildrenAsync() => _loader();

    [RelayCommand]
    private void NewStoredProcedure()
    {
        if (_dialogService is null || _insertQuery is null) return;
        var sql = _dialogService.ShowCreateProcedureDialog();
        if (!string.IsNullOrEmpty(sql))
            _insertQuery(sql);
    }

    [RelayCommand]
    private void NewTable()
    {
        if (_dialogService is null || _insertQuery is null) return;
        var sql = _dialogService.ShowCreateTableDialog();
        if (!string.IsNullOrEmpty(sql))
            _insertQuery(sql);
    }
}
