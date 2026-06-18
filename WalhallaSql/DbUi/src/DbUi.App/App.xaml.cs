using AvalonDock.Themes;
using DbUi.App.Migration;
using DbUi.Core.Connection;
using DbUi.Core.Workspace;
using DbUi.UI.Registration;
using DbUi.UI.ViewModels;
using DbUi.UI.Views;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Serilog;
using System.IO;
using System.Windows;

namespace DbUi.App;

public partial class App : Application
{
    private IHost? _host;

    // Parameterless ctor required by WPF-generated App.g.cs
    public App() { }

    internal void SetHost(IHost host) => _host = host;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        _host!.Start();

        var mainVM = _host!.Services.GetRequiredService<MainViewModel>();
        mainVM.MigrationWindowRequested += () =>
        {
            var migrationWindow = new MigrationWindow { Owner = MainWindow };
            migrationWindow.ShowDialog();
        };

        var mainWindow = _host!.Services.GetRequiredService<MainWindow>();
        mainWindow.ApplyTheme(new Vs2013DarkTheme());
        MainWindow = mainWindow;
        mainWindow.Show();
    }

    protected override async void OnExit(ExitEventArgs e)
    {
        if (_host is not null)
        {
            await _host.StopAsync(TimeSpan.FromSeconds(3));
            _host.Dispose();
        }
        base.OnExit(e);
    }

    internal static IHost BuildHost(string[] args)
    {
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        var logDir = Path.Combine(appData, "DbUi", "logs");
        Directory.CreateDirectory(logDir);

        Log.Logger = new LoggerConfiguration()
            .MinimumLevel.Debug()
            .WriteTo.File(Path.Combine(logDir, "dbui-.log"),
                rollingInterval: RollingInterval.Day,
                retainedFileCountLimit: 7)
            .CreateLogger();

        return Host.CreateDefaultBuilder(args)
            .UseSerilog()
            .ConfigureServices(services =>
            {
                services.AddSingleton<IConnectionStore, JsonConnectionStore>();
                services.AddSingleton<IWorkspaceSessionFactory, WalhallaSqlWorkspaceSessionFactory>();
                services.AddUiServices();
            })
            .Build();
    }
}
