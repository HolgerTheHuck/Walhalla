using AvalonDock.Themes;
using System.Windows;
using System.Windows.Interop;
using System.Runtime.InteropServices;
using DbUi.UI.ViewModels;
using MahApps.Metro.IconPacks;

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
