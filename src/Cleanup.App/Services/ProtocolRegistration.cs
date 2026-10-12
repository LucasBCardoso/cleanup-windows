using Microsoft.Win32;

namespace Cleanup.App.Services;

/// <summary>
/// Registers the <c>cleanup:</c> protocol for this user (<c>HKCU\Software\Classes\cleanup</c>), pointing at this exe.
/// Links only navigate or start read-only scans (design §5.2, invariant 12).
/// </summary>
public static class ProtocolRegistration
{
    public static void EnsureRegistered()
    {
        try
        {
            var exe = Environment.ProcessPath;
            if (exe is null) return;
            var command = $"\"{exe}\" \"%1\"";
            using var key = Registry.CurrentUser.CreateSubKey(@"Software\Classes\cleanup");
            using var open = key.CreateSubKey(@"shell\open\command");
            if (open.GetValue(null) as string == command) return;
            key.SetValue(null, "URL:Cleanup");
            key.SetValue("URL Protocol", "");
            using (var icon = key.CreateSubKey("DefaultIcon")) icon.SetValue(null, $"\"{exe}\",0");
            open.SetValue(null, command);
        }
        catch (Exception e) when (e is UnauthorizedAccessException or IOException or System.Security.SecurityException)
        {
            Log.Write("protocol: " + e.Message);
        }
    }
}
