using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using CommunityToolkit.Mvvm.ComponentModel;
using Cleanup.App.Services;
using Cleanup.Core.Models;
using Cleanup.Core.SelfUpdate;
using Microsoft.UI.Dispatching;

namespace Cleanup.App.ViewModels;

/// <summary>How the app comes back after the installer replaced it.</summary>
public enum Relaunch { None = 0, Window = 1, Background = 2 }

/// <summary>
/// Cleanup updating itself from GitHub Releases. Every 6 h (and 1 min after launch) it reads the latest release,
/// downloads its Setup.exe to <c>%LOCALAPPDATA%\Cleanup\updates</c> and checks it against the SHA-256 GitHub published.
/// The silent install runs only when nobody is using the window: right away in the tray, when the window is closed
/// to the tray, when Cleanup quits, or from Ajustes › "Instalar e reiniciar". Only a copy installed by Setup.exe
/// updates itself, so development builds and the zip are left alone.
/// </summary>
public sealed partial class SelfUpdateViewModel : ObservableObject
{
    public static readonly TimeSpan CheckInterval = TimeSpan.FromHours(6);
    public static readonly TimeSpan FirstCheck = TimeSpan.FromMinutes(1);

    private static readonly HttpClient Http = CreateClient();
    private readonly AppSettings settings = AppServices.Shared.Settings;
    private readonly DispatcherQueue dispatcher;
    private Timer? timer;
    private string? readyPath;
    private bool installing;
    private Version? notified;
    /// <summary>Why the last check or install failed; shown until the next check.</summary>
    private string? lastError;

    [ObservableProperty] private bool isChecking;
    [ObservableProperty] private string statusText = "";

    /// <summary>The downloaded, verified release waiting to be installed.</summary>
    [ObservableProperty] private AppRelease? ready;

    public SelfUpdateViewModel(DispatcherQueue dispatcher)
    {
        this.dispatcher = dispatcher;
        UpdateStatus();
    }

    public static Version? CurrentVersion => typeof(App).Assembly.GetName().Version;
    public static string CurrentVersionText => CurrentVersion?.ToString(3) ?? "?";

    public static bool CanUpdateItself =>
        File.Exists(Path.Combine(AppContext.BaseDirectory, "unins000.exe")) && string.IsNullOrEmpty(Environment.GetEnvironmentVariable("CLEANUP_INSTANCE"));

    public Uri ReleasePage => Ready?.Page ?? ReleaseFeed.ReleasesPage;

    private static string Folder => Path.Combine(AppPaths.SupportDirectory, "updates");

    public void Start() => timer ??= new Timer(_ => dispatcher.TryEnqueue(() => _ = CheckAsync(automatic: true)), null, FirstCheck, CheckInterval);

    /// <summary>The periodic check, or "Procurar agora" in Ajustes (which also runs with the automatic updates off).</summary>
    public async Task CheckAsync(bool automatic)
    {
        if (IsChecking || !CanUpdateItself || (automatic && !settings.AutoUpdate)) return;
        IsChecking = true;
        lastError = null;
        StatusText = "Procurando atualização…";
        try
        {
            var release = await Task.Run(FetchLatestAsync);
            settings.UpdateLastCheck = DateTimeOffset.Now;
            settings.Save();
            if (release is null || !ReleaseFeed.IsNewer(release, CurrentVersion))
            {
                Ready = null;
                UpdateStatus();
                return;
            }
            if (Ready?.Version != release.Version || readyPath is null)
            {
                StatusText = $"Baixando o Cleanup {release.Version.ToString(3)}…";
                readyPath = await Task.Run(() => DownloadAsync(release));
                Ready = release;
            }
            UpdateStatus();
            if (automatic && !InstallWhenIdle()) Announce(release);
        }
        catch (Exception e) when (e is HttpRequestException or IOException or UnauthorizedAccessException or TaskCanceledException or InvalidDataException)
        {
            Log.Write("self-update: " + e.Message);
            lastError = e is TaskCanceledException ? "O GitHub não respondeu a tempo. O Cleanup tenta de novo mais tarde." : "Não foi possível atualizar agora: " + e.Message;
            UpdateStatus();
        }
        finally { IsChecking = false; }
    }

    /// <summary>Installs a ready update when the window is hidden and nothing is running; the app quits and comes back in the tray.</summary>
    public bool InstallWhenIdle()
    {
        if (Ready is null || !settings.AutoUpdate || IsBusy || App.Window?.AppWindow.IsVisible == true) return false;
        if (!Install(Relaunch.Background)) return false;
        App.Quit();
        return true;
    }

    /// <summary>Cleanup is quitting: a ready update installs now, without reopening the app.</summary>
    public void InstallOnQuit()
    {
        if (settings.AutoUpdate && !IsBusy) Install(Relaunch.None);
    }

    /// <summary>"Instalar e reiniciar" in Ajustes.</summary>
    public void InstallNow()
    {
        if (Install(Relaunch.Window)) App.Quit();
    }

    private static bool IsBusy => AppViewModels.Scan.IsScanning || AppViewModels.Scan.IsCleaning || AppViewModels.AutoClean?.IsRunning == true;

    private bool Install(Relaunch relaunch)
    {
        if (installing || Ready is not { } release || readyPath is null) return false;
        try
        {
            // The updates folder belongs to the user: check the bytes again right before running them.
            if (!File.Exists(readyPath) || !ReleaseFeed.Matches(readyPath, release.Sha256))
            {
                Log.Write("self-update: the downloaded installer changed; discarded");
                Ready = null;
                readyPath = null;
                UpdateStatus();
                return false;
            }
            var start = new ProcessStartInfo(readyPath) { UseShellExecute = false };
            foreach (var arg in new[] { "/VERYSILENT", "/SUPPRESSMSGBOXES", "/NORESTART", "/CLOSEAPPLICATIONS", $"/RELAUNCH={(int)relaunch}" })
                start.ArgumentList.Add(arg);
            Process.Start(start);
            installing = true;
            Log.Write($"self-update: installing {release.Version.ToString(3)} (relaunch {relaunch})");
            return true;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception)
        {
            Log.Write("self-update: " + e.Message);
            lastError = "Não foi possível abrir o instalador: " + e.Message;
            UpdateStatus();
            return false;
        }
    }

    /// <summary>The window is open, so the install waits: say so once per version.</summary>
    private void Announce(AppRelease release)
    {
        if (notified == release.Version || !settings.NotificationsEnabled) return;
        notified = release.Version;
        Notifications.Show($"Cleanup {release.Version.ToString(3)} está pronto",
            "A atualização é instalada quando você fechar a janela. Para instalar agora, use Ajustes › Atualizações.", "cleanup://home");
    }

    public void UpdateStatus()
    {
        if (lastError is not null)
        {
            StatusText = lastError;
            return;
        }
        if (!CanUpdateItself)
        {
            StatusText = $"Versão {CurrentVersionText}. Esta cópia não foi instalada pelo Setup, então não se atualiza sozinha.";
            return;
        }
        if (Ready is { } release)
        {
            StatusText = settings.AutoUpdate
                ? $"O Cleanup {release.Version.ToString(3)} já foi baixado e conferido. Ele é instalado quando você fechar a janela, ou agora pelo botão."
                : $"O Cleanup {release.Version.ToString(3)} já foi baixado e conferido. Para instalar, use o botão.";
            return;
        }
        var last = settings.UpdateLastCheck is { } at ? $"Última verificação: {at.LocalDateTime:dd/MM HH:mm}, sem versão nova." : "Ainda não verificou.";
        StatusText = $"Você está na versão {CurrentVersionText}. {last}";
    }

    private static async Task<AppRelease?> FetchLatestAsync()
    {
        using var cancel = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        using var request = new HttpRequestMessage(HttpMethod.Get, ReleaseFeed.LatestUrl);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        using var response = await Http.SendAsync(request, cancel.Token);
        if (response.StatusCode == HttpStatusCode.NotFound) return null;
        response.EnsureSuccessStatusCode();
        var release = ReleaseFeed.Parse(await response.Content.ReadAsStringAsync(cancel.Token));
        if (release is null) Log.Write("self-update: the latest release has no trusted installer");
        return release;
    }

    /// <summary>Downloads to a .part file and keeps it only when the SHA-256 matches; older downloads are removed.</summary>
    private static async Task<string> DownloadAsync(AppRelease release)
    {
        Directory.CreateDirectory(Folder);
        var path = Path.Combine(Folder, release.InstallerName);
        if (File.Exists(path) && ReleaseFeed.Matches(path, release.Sha256)) return path;
        foreach (var old in Directory.GetFiles(Folder))
        {
            try { File.Delete(old); } catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        }
        var part = path + ".part";
        using (var cancel = new CancellationTokenSource(TimeSpan.FromMinutes(20)))
        using (var response = await Http.GetAsync(release.Installer, HttpCompletionOption.ResponseHeadersRead, cancel.Token))
        {
            response.EnsureSuccessStatusCode();
            await using var file = File.Create(part);
            await response.Content.CopyToAsync(file, cancel.Token);
        }
        if (!ReleaseFeed.Matches(part, release.Sha256))
        {
            File.Delete(part);
            throw new InvalidDataException("o arquivo baixado não confere com o SHA-256 publicado no GitHub.");
        }
        File.Move(part, path, overwrite: true);
        return path;
    }

    private static HttpClient CreateClient()
    {
        // GitHub's API refuses requests without a User-Agent.
        var client = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
        client.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("Cleanup-Windows", CurrentVersion?.ToString(3) ?? "0"));
        return client;
    }
}
