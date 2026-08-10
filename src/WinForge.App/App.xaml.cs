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

        // Log the full diagnostic chain (type, message, every inner exception,
        // stack trace, and XAML line/position when present) so a XamlParseException
        // reports its true root cause instead of only the outer wrapper. The
        // user-facing dialog intentionally shows only the top-level message.
        logger.Error($"Fatal error ({source}): {message}");
        logger.Error(BuildDetailedDiagnostics(ex, source));

        // Coalesce repeated fatal errors into at most one user-visible dialog. A single
        // root cause (e.g. a binding/render that keeps throwing after entering a step)
        // must never generate an unbounded storm of MessageBoxes — that storm can
        // escalate into a process-terminating stack overflow (0xc00000fd). The error is
        // always logged; only rapid repeats and the total dialog count are throttled.
        if (ErrorDialogGuard.ShouldShow($"{source}:{message}"))
        {
            MessageBox.Show(
                $"WinForge encountered an unexpected error and recovered.\n\n{message}\n\nDetails have been recorded in the Logs page.",
                "WinForge — Unexpected Error",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        }
    }

    /// <summary>
    /// Walks the entire exception chain and renders type / message / inner / stack,
    /// surfacing <see cref="System.Windows.Markup.XamlParseException"/> line and
    /// position when available. Used for log-only diagnostics.
    /// </summary>
    private static string BuildDetailedDiagnostics(Exception? ex, string source, int depth = 0)
    {
        if (ex is null)
        {
            return string.Empty;
        }

        var sb = new System.Text.StringBuilder();
        sb.AppendLine($"[diag:{source}] depth={depth} type={ex.GetType().FullName}");
        sb.AppendLine($"  message: {ex.Message}");
        if (ex is System.Windows.Markup.XamlParseException xamlEx)
        {
            sb.AppendLine($"  xaml: line={xamlEx.LineNumber} pos={xamlEx.LinePosition}");
        }
        sb.AppendLine($"  stack: {ex.StackTrace}");

        if (ex.InnerException is not null)
        {
            sb.Append(BuildDetailedDiagnostics(ex.InnerException, source, depth + 1));
        }

        return sb.ToString();
    }
}
