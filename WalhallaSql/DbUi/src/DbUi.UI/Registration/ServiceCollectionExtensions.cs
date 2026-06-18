using DbUi.UI.Services;
using DbUi.UI.ViewModels;
using DbUi.UI.Views;
using Microsoft.Extensions.DependencyInjection;

namespace DbUi.UI.Registration;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddUiServices(this IServiceCollection services)
    {
        services.AddSingleton<IDialogService, WpfDialogService>();
        services.AddSingleton<MainViewModel>();
        services.AddTransient<MainWindow>();
        return services;
    }
}
