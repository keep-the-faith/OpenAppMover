using OpenAppMover.Gui.Services;
using OpenAppMover.Host;
using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.Extensions.Logging;

namespace OpenAppMover.Gui.ViewModels;

/// <summary>A tab: runs its long operations in the background with a busy indicator.</summary>
public abstract partial class PageViewModel(OpenAppMoverHost host, IDialogService dialogs, ILogger logger) : ObservableObject
{
    protected OpenAppMoverHost Host => host;
    protected IDialogService Dialogs => dialogs;

    [ObservableProperty]
    private bool _isBusy;

    [ObservableProperty]
    private string? _busyMessage;

    /// <summary>Runs <paramref name="work"/> on a background thread, then <paramref name="done"/> on the UI thread.</summary>
    protected async Task RunAsync<T>(string message, Func<T> work, Action<T> done)
    {
        IsBusy = true;
        BusyMessage = message;
        try
        {
            var result = await Task.Run(work);
            done(result);
        }
        catch (Exception e)
        {
            logger.LogError(e, "{Operation} failed", message);
            dialogs.Error("OpenAppMover", e.Message);
        }
        finally
        {
            IsBusy = false;
            BusyMessage = null;
        }
    }
}
