using System.Diagnostics;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Text.Json;
using Cleanup.Core.Admin;
using Cleanup.Elevated;

// Cleanup.Elevated.exe — the UAC helper (design §7). Started by Cleanup with `runas`, it connects to the app's pipe,
// runs an allow-listed batch and answers with one result per step. Nothing arbitrary: paths are re-checked with
// AdminPathPolicy built from this process's own view of the system folders, and operations are a fixed list.
if (args.Length != 4 || args[0] != "--pipe" || args[2] != "--parent" || !int.TryParse(args[3], out var parentId))
{
    Console.Error.WriteLine("Cleanup.Elevated is started by Cleanup; it isn't meant to be run by hand.");
    return 64;
}

using var pipe = new NamedPipeClientStream(".", args[1], PipeDirection.InOut);
try { pipe.Connect(10_000); }
catch (TimeoutException) { return 2; }

// Only talk to a Cleanup process from this same folder (the parent that started us).
if (!Native.GetNamedPipeServerProcessId(pipe.SafePipeHandle.DangerousGetHandle(), out var serverId) || serverId != parentId
    || !IsSibling(serverId))
{
    return 3;
}

using var reader = new StreamReader(pipe, leaveOpen: true);
using var writer = new StreamWriter(pipe, leaveOpen: true) { AutoFlush = true };
var line = reader.ReadLine();
var request = line is null ? null : JsonSerializer.Deserialize<Elevation.Request>(line, Elevation.Json);
if (request is null) return 4;

var results = request.Steps.Select(Steps.Run).ToList();
writer.WriteLine(JsonSerializer.Serialize(new Elevation.Response(results), Elevation.Json));
return 0;

static bool IsSibling(uint processId)
{
    try
    {
        using var process = Process.GetProcessById((int)processId);
        var path = process.MainModule?.FileName;
        return path is not null && string.Equals(Path.GetDirectoryName(path), AppContext.BaseDirectory.TrimEnd('\\'),
            StringComparison.OrdinalIgnoreCase);
    }
    catch (Exception e) when (e is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception)
    {
        return false;
    }
}

namespace Cleanup.Elevated
{
    internal static partial class Native
    {
        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool GetNamedPipeServerProcessId(IntPtr pipe, out uint serverProcessId);
    }

    internal static class Steps
    {
        private static readonly AdminPathPolicy Policy = new(
            Environment.GetFolderPath(Environment.SpecialFolder.Windows),
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData));

        public static Elevation.StepResult Run(Elevation.Step step)
        {
            try
            {
                return step.Op switch
                {
                    Elevation.Ops.Remove => Remove(step.Path),
                    Elevation.Ops.FlushDns => Exec(System32("ipconfig.exe"), ["/flushdns"]),
                    Elevation.Ops.Run => Maintenance(step.Id),
                    Elevation.Ops.WifiDelete => WiFiDelete(step.Id),
                    _ => new(false, "operação não permitida"),
                };
            }
            catch (Exception e)
            {
                return new(false, e.Message);
            }
        }

        private static Elevation.StepResult Remove(string? path)
        {
            if (path is null) return new(false, "sem caminho");
            if (Policy.Reject(path) is { } reason) return new(false, "recusado: " + reason);
            // By handle, never by path: these roots are user-writable, so a folder swapped for a junction mid-delete
            // must be removed as a link, not followed (HandleDelete).
            Cleanup.Platform.Windows.HandleDelete.Delete(path);
            return new(true, "removido");
        }

        /// <summary>System tools by full path: an elevated process must never pick up an exe from its own or current folder.</summary>
        private static string System32(string exe) => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), exe);

        private static Elevation.StepResult Maintenance(string? id)
        {
            var system32 = Environment.GetFolderPath(Environment.SpecialFolder.System);
            return id switch
            {
                "dism-cleanup" => Exec(Path.Combine(system32, "Dism.exe"), ["/Online", "/Cleanup-Image", "/StartComponentCleanup"]),
                "sfc" => Exec(Path.Combine(system32, "sfc.exe"), ["/scannow"]),
                "reset-search" => ResetSearch(),
                "free-ram" => FreeRam(),
                _ => new(false, "tarefa não permitida"),
            };
        }

        private static Elevation.StepResult Exec(string file, string[] arguments)
        {
            var start = new ProcessStartInfo(file, arguments)
            {
                UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true,
            };
            using var process = Process.Start(start)!;
            var output = process.StandardOutput.ReadToEndAsync();
            var error = process.StandardError.ReadToEndAsync();
            process.WaitForExit();
            var text = (output.Result + error.Result).Trim();
            if (text.Length > 2000) text = text[^2000..];
            return new(process.ExitCode == 0, process.ExitCode == 0 ? text : $"código {process.ExitCode}: {text}");
        }

        /// <summary>
        /// Forgets one saved Wi-Fi network. The name is re-validated here, must be a saved profile right now, and is
        /// refused when it's the network in use (read again as administrator, not trusted from the app). It reaches
        /// netsh as a single argument; no shell is involved.
        /// </summary>
        private static Elevation.StepResult WiFiDelete(string? name)
        {
            if (name is null) return new(false, "sem nome");
            if (Cleanup.Core.Privacy.WiFiNetworks.Refusal(name) is { } reason) return new(false, "recusado: " + reason);
            var (_, interfaces) = Netsh(Cleanup.Core.Privacy.WiFiNetworks.ShowInterfacesArguments);
            if (Cleanup.Core.Privacy.WiFiNetworks.Connected(interfaces).Contains(name))
                return new(false, Cleanup.Core.Privacy.WiFiNetworks.InUseMessage);
            var (listed, profiles) = Netsh(Cleanup.Core.Privacy.WiFiNetworks.ShowProfilesArguments);
            if (listed != 0 || !Cleanup.Core.Privacy.WiFiNetworks.Profiles(profiles).Contains(name, StringComparer.Ordinal))
                return new(false, "rede não encontrada");
            var (code, output) = Netsh(Cleanup.Core.Privacy.WiFiNetworks.DeleteArguments(name));
            output = output.Trim();
            return code == 0 ? new(true, output) : new(false, $"código {code}: {output}");
        }

        private static (int Code, string Output) Netsh(IReadOnlyList<string> arguments)
        {
            System.Text.Encoding.RegisterProvider(System.Text.CodePagesEncodingProvider.Instance);
            var start = new ProcessStartInfo(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "netsh.exe"))
            {
                UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true,
            };
            foreach (var argument in arguments) start.ArgumentList.Add(argument);
            using var process = Process.Start(start)!;
            using var buffer = new MemoryStream();
            var copy = process.StandardOutput.BaseStream.CopyToAsync(buffer);
            var error = process.StandardError.ReadToEndAsync();
            process.WaitForExit();
            copy.Wait();
            _ = error.Result;
            System.Text.Encoding oem;
            try { oem = System.Text.Encoding.GetEncoding(System.Globalization.CultureInfo.CurrentCulture.TextInfo.OEMCodePage); }
            catch (Exception e) when (e is ArgumentException or NotSupportedException) { oem = System.Text.Encoding.Latin1; }
            return (process.ExitCode, Cleanup.Core.Privacy.WiFiNetworks.Decode(buffer.ToArray(), oem));
        }

        private static Elevation.StepResult ResetSearch()
        {
            var stop = Exec(System32("net.exe"), ["stop", "WSearch"]);
            if (!stop.Ok && !stop.Message.Contains("3521", StringComparison.Ordinal)) return stop;
            var data = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
                @"Microsoft\Search\Data\Applications\Windows");
            foreach (var file in Directory.Exists(data) ? Directory.GetFiles(data, "Windows.*") : [])
            {
                try { File.Delete(file); } catch (IOException) { }
            }
            return Exec(System32("net.exe"), ["start", "WSearch"]);
        }

        /// <summary>Empties the working sets of all processes and the standby list (what RAMMap's "Empty" does).</summary>
        private static Elevation.StepResult FreeRam()
        {
            MemoryNative.EnablePrivilege("SeProfileSingleProcessPrivilege");
            MemoryNative.EnablePrivilege("SeIncreaseQuotaPrivilege");
            var emptied = 0;
            foreach (var p in Process.GetProcesses())
            {
                try { if (MemoryNative.EmptyWorkingSet(p.Handle)) emptied++; }
                catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException) { }
                finally { p.Dispose(); }
            }
            var status = MemoryNative.PurgeStandbyList();
            return status == 0
                ? new(true, $"memória liberada ({emptied} processos)")
                : new(false, $"lista de espera não esvaziada (0x{status:X8})");
        }
    }

    internal static class MemoryNative
    {
        [DllImport("psapi.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool EmptyWorkingSet(IntPtr hProcess);

        [DllImport("ntdll.dll")]
        private static extern int NtSetSystemInformation(int infoClass, ref int info, int length);

        private const int SystemMemoryListInformation = 80;
        private const int MemoryPurgeStandbyList = 4;

        public static int PurgeStandbyList()
        {
            var command = MemoryPurgeStandbyList;
            return NtSetSystemInformation(SystemMemoryListInformation, ref command, sizeof(int));
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct TokenPrivileges
        {
            public int Count;
            public long Luid;
            public int Attributes;
        }

        [DllImport("advapi32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool OpenProcessToken(IntPtr process, int access, out IntPtr token);

        [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool LookupPrivilegeValue(string? system, string name, out long luid);

        [DllImport("advapi32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool AdjustTokenPrivileges(IntPtr token, [MarshalAs(UnmanagedType.Bool)] bool disableAll,
            ref TokenPrivileges state, int length, IntPtr previous, IntPtr returnLength);

        [DllImport("kernel32.dll")]
        private static extern bool CloseHandle(IntPtr handle);

        public static void EnablePrivilege(string name)
        {
            if (!OpenProcessToken(Process.GetCurrentProcess().Handle, 0x20 | 0x8, out var token)) return;
            try
            {
                if (!LookupPrivilegeValue(null, name, out var luid)) return;
                var state = new TokenPrivileges { Count = 1, Luid = luid, Attributes = 2 };
                AdjustTokenPrivileges(token, false, ref state, 0, IntPtr.Zero, IntPtr.Zero);
            }
            finally { CloseHandle(token); }
        }
    }
}
