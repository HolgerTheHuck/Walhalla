using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Runtime.InteropServices;
using AvalonDock.Themes;
using DbUi.UI.Behaviors;
using DbUi.UI.ViewModels;
using DbUi.UI.ViewModels.Dialogs;
using MahApps.Metro.IconPacks;
using WpfDataGrid = System.Windows.Controls.DataGrid;
using WpfDataGridCell = System.Windows.Controls.DataGridCell;

namespace DbUi.UI.Views;

public partial class MainWindow : Window
{
    private readonly MainViewModel _vm;

    public MainWindow(MainViewModel viewModel)
    {
        _vm = viewModel;
        DataContext = viewModel;
        InitializeComponent();
        StateChanged += (_, _) => UpdateMaxRestoreIcon();
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        ApplyDwmDarkMode();
    }

    protected override async void OnClosed(EventArgs e)
    {
        base.OnClosed(e);
        await _vm.DisposeAsync();
    }

    public void ApplyTheme(Theme theme) => DockManager.Theme = theme;

    private void MinimizeButton_Click(object sender, RoutedEventArgs e) =>
        WindowState = WindowState.Minimized;

    private void MaxRestoreButton_Click(object sender, RoutedEventArgs e) =>
        WindowState = WindowState == WindowState.Maximized
            ? WindowState.Normal
            : WindowState.Maximized;

    private void CloseButton_Click(object sender, RoutedEventArgs e) => Close();

    private void MenuExit_Click(object sender, RoutedEventArgs e) => Close();

    private void UpdateMaxRestoreIcon()
    {
        if (MaxRestoreButton.Content is PackIconMaterial icon)
            icon.Kind = WindowState == WindowState.Maximized
                ? PackIconMaterialKind.WindowRestore
                : PackIconMaterialKind.WindowMaximize;
    }

    private void OnResultDataGridDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (sender is not WpfDataGrid grid) return;
        if (e.OriginalSource is not DependencyObject source) return;

        var cell = FindParentOfType<WpfDataGridCell>(source);
        if (cell == null || cell.Column == null) return;

        if (grid.SelectedCells.Count == 0)
            return;

        var selectedCell = grid.SelectedCells[0];
        if (selectedCell.Item is not object?[] row)
            return;

        var columnName = selectedCell.Column.Header?.ToString() ?? "Value";
        var columnIndex = DataGridColumnsBehavior.GetColumnIndex(selectedCell.Column);
        var value = columnIndex >= 0 && columnIndex < row.Length ? row[columnIndex] : null;

        var vm = new ValueInspectorViewModel(columnName, value);
        var dialog = new ValueInspectorDialog { DataContext = vm, Owner = this };
        dialog.ShowDialog();
    }

    private static T? FindParentOfType<T>(DependencyObject? child) where T : DependencyObject
    {
        while (child != null)
        {
            if (child is T parent) return parent;
            child = VisualTreeHelper.GetParent(child);
        }
        return null;
    }

    private void ApplyDwmDarkMode()
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        // Dark title bar text/icons (Windows 10 1903+)
        int dark = 1;
        DwmSetWindowAttribute(hwnd, 20, ref dark, sizeof(int));
        // Exact caption background color: #2D2D30  (COLORREF = 0x00BBGGRR)
        int captionColor = 0x00302D2D;
        DwmSetWindowAttribute(hwnd, 35, ref captionColor, sizeof(int));
        // Caption text color: #DCDCDC
        int textColor = 0x00DCDCDC;
        DwmSetWindowAttribute(hwnd, 36, ref textColor, sizeof(int));
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(
        IntPtr hwnd, int dwAttribute, ref int pvAttribute, int cbAttribute);
}
