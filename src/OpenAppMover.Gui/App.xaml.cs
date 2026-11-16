using System.IO;
using System.Windows;
using OpenAppMover.Gui.Services;
using OpenAppMover.Gui.ViewModels;
using OpenAppMover.Gui.Views;
using OpenAppMover.Host;
using Microsoft.Extensions.Logging;
using Serilog;

namespace OpenAppMover.Gui;

public partial class App
{
    private ILoggerFactory? _loggerFactory;
    private MainViewModel? _main;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        var logDirectory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "OpenAppMover", "logs");
        Log.Logger = new LoggerConfiguration()
            .MinimumLevel.Debug()
            .WriteTo.File(Path.Combine(logDirectory, "openappmover-gui-.log"), rollingInterval: RollingInterval.Day, retainedFileCountLimit: 14)
            .CreateLogger();
        _loggerFactory = LoggerFactory.Create(builder => builder.AddSerilog(dispose: true));
        var logger = _loggerFactory.CreateLogger("OpenAppMover.Gui");
        logger.LogInformation("OpenAppMover GUI started: {Args}", string.Join(" ", e.Args));

        DispatcherUnhandledException += (_, args) =>
        {
            logger.LogError(args.Exception, "Unexpected error");
            MessageBox.Show(args.Exception.Message, "OpenAppMover", MessageBoxButton.OK, MessageBoxImage.Error);
            args.Handled = true;
        };

        var host = new OpenAppMoverHost(_loggerFactory);
        var dialogs = new WpfDialogService();

        // Called by "Apps & features": openappmover-gui.exe uninstall <id> [--yes]
        if (e.Args.Length >= 2 && e.Args[0].Equals("uninstall", StringComparison.OrdinalIgnoreCase))
        {
            Shutdown(UninstallMode.Run(host, dialogs, e.Args[1], e.Args.Contains("--yes", StringComparer.OrdinalIgnoreCase)));
            return;
        }

        _main = new MainViewModel(host, dialogs, logger);
        var window = new MainWindow { DataContext = _main };
        MainWindow = window;
        window.Show();
        _main.Export.RefreshCommand.Execute(null);
        _main.Deployments.RefreshCommand.Execute(null);
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _main?.Dispose();
        _loggerFactory?.Dispose();
        base.OnExit(e);
    }
}

/// <summary>Uninstallation started from "Apps &amp; features", without the main window.</summary>
internal static class UninstallMode
{
    public static int Run(OpenAppMoverHost host, IDialogService dialogs, string id, bool quiet)
    {
        var matches = host.FindDeployments(id);
        if (matches.Count != 1)
        {
            if (!quiet)
                dialogs.Error("OpenAppMover", $"Deployment not found: {id}");
            return 3;
        }

        var record = matches[0];
        if (!quiet && !dialogs.Confirm("OpenAppMover", $"Uninstall {record.AppName}?"))
            return 0;

        var failures = host.Uninstall(record);
        if (quiet)
            return failures.Count == 0 ? 0 : 1;
        if (failures.Count == 0)
        {
            dialogs.Info("OpenAppMover", $"{record.AppName} has been uninstalled.");
            return 0;
        }
        dialogs.Error("OpenAppMover", $"{failures.Count} change(s) could not be reverted:{Environment.NewLine}" +
                                  string.Join(Environment.NewLine, failures.Take(20)));
        return 1;
    }
}
