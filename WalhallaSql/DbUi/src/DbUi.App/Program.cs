using System.Threading;
using System.Windows;

namespace DbUi.App;

public static class Program
{
    [STAThread]
    public static void Main(string[] args)
    {
        // Single-instance guard
        using var mutex = new Mutex(true, "Global\\DbUi_SingleInstance", out var isNewInstance);
        if (!isNewInstance)
        {
            System.Windows.MessageBox.Show("DbUi is already running.", "DbUi",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var host = App.BuildHost(args);
        var app = new App();
        app.SetHost(host);
        app.InitializeComponent();
        app.Run();
    }
}
