using System;
using System.Threading.Tasks;
using System.Windows;
using Microsoft.Extensions.DependencyInjection;
using WinForge.App.Localization;
using WinForge.App.Services;
using WinForge.App.ViewModels;
using WinForge.Core.Services;

namespace WinForge.App;

/// <summary>
/// Application entry point. Builds the dependency container, shows the main
/// window with its view model, and installs process-wide error handlers so a
/// thrown exception never silently terminates the app.
/// </summary>
public partial class App : Application
{
    private IServiceProvider _provider = null!;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        _provider = Bootstrapper.Build();

        // Apply the persisted (or OS-default, falling back to English) language
        // and expose the localization service to XAML as the "Loc" resource so
        // every view binds localized strings through it (no scattered language branches).
        var localization = _provider.GetRequiredService<ILocalizationService>();
        var languageStore = _provider.GetRequiredService<ILanguageSettingsStore>();
        LocalizationBootstrap.Initialize(localization, languageStore);
        Application.Current.Resources["Loc"] = localization;

        var logger = _provider.GetRequiredService<ILoggerService>();

        var mainWindow = new MainWindow
        {
            DataContext = _provider.GetRequiredService<MainViewModel>()
        };
        MainWindow = mainWindow;
        mainWindow.Show();

        logger.Info("Application started");

        WireGlobalErrorHandlers(logger);
    }

    private void WireGlobalErrorHandlers(ILoggerService logger)
    {
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
            HandleFatal(logger, args.ExceptionObject as Exception, "AppDomain.UnhandledException");

        DispatcherUnhandledException += (_, args) =>
        {
            HandleFatal(logger, args.Exception, "DispatcherUnhandledException");
            args.Handled = true;
        };

        TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            logger.Error($"Unobserved task exception: {args.Exception.Message}");
            args.SetObserved();
        };
    }

    private static void HandleFatal(ILoggerService logger, Exception? ex, string source)
    {
        var message = ex?.Message ?? "Unknown error";
        logger.Error($"Fatal error ({source}): {message}");

        MessageBox.Show(
            $"WinForge encountered an unexpected error and recovered.\n\n{message}\n\nDetails have been recorded in the Logs page.",
            "WinForge — Unexpected Error",
            MessageBoxButton.OK,
            MessageBoxImage.Error);
    }
}
