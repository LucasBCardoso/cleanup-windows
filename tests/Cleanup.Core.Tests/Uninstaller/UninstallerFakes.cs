using Cleanup.Core.Models;
using Cleanup.Core.Uninstaller;

namespace Cleanup.Core.Tests.Uninstaller;

public static class Apps
{
    public static InstalledApp Win32(string name, string? publisher = null, string? folder = null, string? exe = null, bool system = false,
        string? id = null) => new()
    {
        Id = id ?? "win32:hkcu:" + name.ToLowerInvariant(), Name = name, Kind = AppKind.Win32, Publisher = publisher, InstallFolder = folder,
        MainExecutable = exe, IsSystemApp = system, UninstallString = @"C:\x\uninst.exe",
    };

    public static InstalledApp Store(string name, string family) => new()
    {
        Id = "store:" + family, Name = name, Kind = AppKind.Store, PackageFamilyName = family, PackageFullName = family.Replace("_", "_1.0.0.0_x64__"),
    };
}

/// <summary>An in-memory HKCU: answers subkey listings and records exports and deletes in order.</summary>
public sealed class FakeRegistry : IRegistryBrowser, IRegistryStore
{
    public HashSet<string> Keys { get; } = new(StringComparer.OrdinalIgnoreCase);
    public List<string> Log { get; } = [];
    public HashSet<string> FailExport { get; } = new(StringComparer.OrdinalIgnoreCase);

    public FakeRegistry(params string[] keys)
    {
        foreach (var k in keys) Add(k);
    }

    public void Add(string relative)
    {
        var path = RegistryPolicy.UserSoftware;
        foreach (var part in relative.Split('\\'))
        {
            path += "\\" + part;
            Keys.Add(path);
        }
    }

    public IReadOnlyList<string> SubKeyNames(string key) => Keys
        .Where(k => k.StartsWith(key + "\\", StringComparison.OrdinalIgnoreCase) && !k[(key.Length + 1)..].Contains('\\'))
        .Select(k => k[(key.Length + 1)..]).OrderBy(k => k).ToList();

    public long ApproximateSize(string key) => 100;

    public bool Exists(string key) => Keys.Contains(key);

    public void Export(string key, string file)
    {
        if (FailExport.Contains(key)) throw new IOException("disco cheio");
        File.WriteAllText(file, RegExport.Header);
        Log.Add("export " + key);
    }

    public void DeleteTree(string key)
    {
        Log.Add("delete " + key);
        Keys.RemoveWhere(k => k.Equals(key, StringComparison.OrdinalIgnoreCase) || k.StartsWith(key + "\\", StringComparison.OrdinalIgnoreCase));
    }
}

/// <summary>Records uninstall / reset calls; the app "goes away" only when told to.</summary>
public sealed class FakePlatform : IAppPlatform
{
    public bool Running { get; set; }
    public bool RemovesApp { get; set; } = true;
    public bool Installed { get; set; } = true;
    public List<string> Calls { get; } = [];

    public bool IsRunning(InstalledApp app) => Running;
    public bool IsInstalled(InstalledApp app) => Installed;

    public Task<UninstallerRun> UninstallAsync(InstalledApp app, CancellationToken cancellation)
    {
        Calls.Add("uninstall " + app.Name);
        if (RemovesApp) Installed = false;
        return Task.FromResult(new UninstallerRun(UninstallerExit.Finished));
    }

    public Task ResetPackageAsync(InstalledApp app, CancellationToken cancellation)
    {
        Calls.Add("reset " + app.Name);
        return Task.CompletedTask;
    }
}
