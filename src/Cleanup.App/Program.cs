using Cleanup.App.Services;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.Windows.AppLifecycle;

namespace Cleanup.App;

/// <summary>
/// Single instance: a second launch (for example a <c>cleanup:</c> link from the Run box, a shortcut or a script)
/// hands its arguments to the window that is already open and exits.
/// </summary>
public static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        WinRT.ComWrappersSupport.InitializeComWrappers();
        // CLEANUP_INSTANCE lets development copies run side by side (and leaves the cleanup: protocol alone).
        var instance = Environment.GetEnvironmentVariable("CLEANUP_INSTANCE");
        var main = AppInstance.FindOrRegisterForKey("Cleanup.Main" + instance);
        if (!main.IsCurrent)
        {
            var activation = AppInstance.GetCurrent().GetActivatedEventArgs();
            // Redirecting must not block the STA thread it was called on.
            Task.Run(() => main.RedirectActivationToAsync(activation).AsTask()).Wait(TimeSpan.FromSeconds(10));
            return 0;
        }
        main.Activated += (_, e) => App.Window?.DispatcherQueue.TryEnqueue(() => App.Window.FollowLink(LinkFrom(e)));
        App.PendingLink = args.FirstOrDefault(a => a.StartsWith("cleanup:", StringComparison.OrdinalIgnoreCase));
        if (string.IsNullOrEmpty(instance)) ProtocolRegistration.EnsureRegistered();

        Application.Start(_p =>
        {
            SynchronizationContext.SetSynchronizationContext(new DispatcherQueueSynchronizationContext(DispatcherQueue.GetForCurrentThread()));
            _ = new App();
        });
        return 0;
    }

    private static string? LinkFrom(AppActivationArguments e)
    {
        var text = e.Data switch
        {
            Windows.ApplicationModel.Activation.ILaunchActivatedEventArgs launch => launch.Arguments,
            Windows.ApplicationModel.Activation.IProtocolActivatedEventArgs protocol => protocol.Uri?.ToString(),
            _ => null,
        };
        if (text is null) return null;
        var start = text.IndexOf("cleanup:", StringComparison.OrdinalIgnoreCase);
        return start < 0 ? null : text[start..].Trim().Trim('"').Split(' ')[0].Trim('"');
    }
}
