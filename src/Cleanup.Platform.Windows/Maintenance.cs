using System.Diagnostics;
using System.Runtime.InteropServices;
using Cleanup.Core.Admin;
using Cleanup.Core.Maintenance;

namespace Cleanup.Platform.Windows;

/// <summary>
/// Runs maintenance batches: admin steps in <c>Cleanup.Elevated.exe</c> (one UAC prompt per batch), user steps here,
/// as the user — never elevated.
/// </summary>
public sealed class MaintenanceRunner(Func<Elevation.Step, Elevation.StepResult> userStep) : IPrivilegedRunner
{
    public static MaintenanceRunner Current { get; } = new(UserTasks.Run);

    public async Task<PrivilegedRun> RunAsync(IReadOnlyList<Elevation.Step> steps, bool asAdmin, CancellationToken cancellation = default)
    {
        if (steps.Count == 0) return new PrivilegedRun(false, []);
        if (!asAdmin) return new PrivilegedRun(false, await Task.Run(() => steps.Select(userStep).ToList(), cancellation));
        var outcome = await ElevatedRunner.RunAsync(steps, cancellation);
        return outcome.Cancelled ? PrivilegedRun.WasCancelled
            : outcome.Error is { } error ? PrivilegedRun.Failed(error)
            : new PrivilegedRun(false, outcome.Results);
    }
}

/// <summary>The maintenance tasks that run as the user (allow-listed like the helper's: nothing arbitrary).</summary>
public static class UserTasks
{
    public static Elevation.StepResult Run(Elevation.Step step)
    {
        if (step.Op != Elevation.Ops.Run) return new(false, "operação não permitida");
        try
        {
            return step.Id switch
            {
                MaintenanceTask.ClearThumbnails => ThumbnailCacheCleaner.Current().Run(),
                MaintenanceTask.StorageSense => OpenStorageSense(),
                _ => new(false, "tarefa não permitida"),
            };
        }
        catch (Exception e) { return new(false, e.Message); }
    }

    /// <summary>
    /// Windows has no supported way to start Storage Sense from an app (its scheduled task runs as SYSTEM), so this
    /// opens its page in Settings, where "Executar o Sensor de Armazenamento agora" runs it.
    /// </summary>
    private static Elevation.StepResult OpenStorageSense()
    {
        Process.Start(new ProcessStartInfo("ms-settings:storagepolicies") { UseShellExecute = true })?.Dispose();
        return new(true, "Configurações abertas: clique em “Executar o Sensor de Armazenamento agora”.");
    }
}

/// <summary>Stops and starts the Windows shell (Explorer) of the current session.</summary>
public interface IExplorerShell
{
    void Stop();
    void Start();
}

/// <summary>
/// Clears Explorer's thumbnail and icon caches: Explorer keeps them open, so it is asked to exit, the cache files are
/// deleted (they are rebuilt on demand), and Explorer is started again — always, even if deleting failed.
/// </summary>
public sealed class ThumbnailCacheCleaner(string localAppData, IExplorerShell shell)
{
    public static ThumbnailCacheCleaner Current() =>
        new(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), new ExplorerShell());

    public IEnumerable<string> CacheFiles()
    {
        var explorer = Path.Combine(localAppData, @"Microsoft\Windows\Explorer");
        var files = new List<string>();
        if (Directory.Exists(explorer))
        {
            foreach (var pattern in (string[])["thumbcache_*.db", "iconcache_*.db"])
                files.AddRange(Directory.EnumerateFiles(explorer, pattern, new EnumerationOptions { AttributesToSkip = FileAttributes.ReparsePoint }));
        }
        var legacy = Path.Combine(localAppData, "IconCache.db");
        if (File.Exists(legacy) && !File.GetAttributes(legacy).HasFlag(FileAttributes.ReparsePoint)) files.Add(legacy);
        return files;
    }

    public Elevation.StepResult Run()
    {
        var deleted = 0;
        var kept = 0;
        shell.Stop();
        try
        {
            foreach (var file in CacheFiles())
            {
                try { File.Delete(file); deleted++; }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException) { kept++; }
            }
        }
        finally { shell.Start(); }
        var message = $"{deleted} arquivo(s) de cache apagados" + (kept > 0 ? $"; {kept} em uso foram mantidos" : "");
        return new(deleted > 0 || kept == 0, message);
    }
}

/// <summary>
/// The real shell: asks Explorer to exit the way its hidden "Sair do Explorer" menu does (message 0x5B4 to the
/// taskbar), force-ends what is left after a few seconds, and starts it again if Windows didn't.
/// </summary>
public sealed class ExplorerShell : IExplorerShell
{
    public void Stop()
    {
        var tray = FindWindow("Shell_TrayWnd", null);
        if (tray != IntPtr.Zero) PostMessage(tray, 0x5B4, IntPtr.Zero, IntPtr.Zero);
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (DateTime.UtcNow < deadline && SessionExplorers().Count > 0) Thread.Sleep(200);
        foreach (var process in SessionExplorers())
        {
            try { process.Kill(); process.WaitForExit(3000); }
            catch (Exception e) when (e is InvalidOperationException or System.ComponentModel.Win32Exception) { }
            finally { process.Dispose(); }
        }
        Thread.Sleep(500); // let the handles on the cache files close
    }

    public void Start()
    {
        Thread.Sleep(300);
        var running = SessionExplorers();
        var any = running.Count > 0;
        foreach (var p in running) p.Dispose();
        if (!any) Process.Start(new ProcessStartInfo(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "explorer.exe"))
        {
            UseShellExecute = false,
        })?.Dispose();
    }

    private static List<Process> SessionExplorers()
    {
        var session = Process.GetCurrentProcess().SessionId;
        var result = new List<Process>();
        foreach (var p in Process.GetProcessesByName("explorer"))
        {
            if (p.SessionId == session) result.Add(p); else p.Dispose();
        }
        return result;
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr FindWindow(string className, string? windowName);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool PostMessage(IntPtr hwnd, uint message, IntPtr wParam, IntPtr lParam);
}
