using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Management;
using System.Security.Principal;
using RunicArtifex.DevKit.VisualStudio.Core;
using RunicArtifex.DevKit.VisualStudio.ToolWindow;

namespace RunicArtifex.DevKit.VisualStudio.Debugging;

/// <summary>
/// Lists and ends Visual Studio's JavaScript debug adapters (<see cref="JsDebugAdapters"/>) through <c>Win32_Process</c>.
/// Call it on the thread pool: a WMI query takes a few hundred milliseconds.
/// </summary>
internal static class JsAdapterProcesses
{
    private static readonly Lazy<(int Id, string? Root, string? Sid)> self = new(() =>
    {
        using var current = Process.GetCurrentProcess();
        string? path = null;
        try { path = current.MainModule?.FileName; } catch (Exception error) when (error is System.ComponentModel.Win32Exception or InvalidOperationException) { }
        string? sid = null;
        try { using var identity = WindowsIdentity.GetCurrent(); sid = identity.User?.Value; } catch (System.Security.SecurityException) { }
        return (current.Id, JsDebugAdapters.InstallationRoot(path), sid);
    });

    /// <summary>The adapters, sorted for this Visual Studio; null when the process listing failed (the failure is in the Runic SDK pane).</summary>
    public static AdapterCensus? Census()
    {
        var processes = List();
        return processes == null ? null : JsDebugAdapters.Census(processes, self.Value.Id, self.Value.Sid, self.Value.Root);
    }

    /// <summary>Before an attach: ends orphaned adapters that are provably this user's and this installation's, and warns about the others.</summary>
    public static void EndOrphans(string source)
    {
        if (Census() is not { } census) return;
        if (census.Orphaned.Count > 0)
        {
            var ended = census.Orphaned.Where(End).ToList();
            if (ended.Count > 0) SdkOutput.WriteLine($"{source}: {JsDebugAdapters.EndedOrphansMessage(ended)}");
        }
        if (census.Unclear.Count > 0) SdkOutput.WriteLine($"{source}: {JsDebugAdapters.UnclearMessage(census.Unclear)}");
    }

    private static long lastOrphanCheck;

    /// <summary><see cref="EndOrphans"/> at most every 30 seconds: an attach tries again every half second until the page is listed.</summary>
    public static void EndOrphansOnce(string source)
    {
        long now = DateTime.UtcNow.Ticks;
        long last = System.Threading.Interlocked.Read(ref lastOrphanCheck);
        if (now - last < TimeSpan.TicksPerSecond * 30) return;
        System.Threading.Interlocked.Exchange(ref lastOrphanCheck, now);
        EndOrphans(source);
    }

    /// <summary>The running node.exe processes, to tell the adapter a debugger call starts from those that ran before (no WMI: cheap).</summary>
    public static IReadOnlyCollection<int> NodeIdsNow()
    {
        var processes = Process.GetProcessesByName("node");
        try { return new HashSet<int>(processes.Select(process => process.Id)); }
        finally { foreach (var process in processes) process.Dispose(); }
    }

    /// <summary>Ends the process if it is still the one listed (same ID and start time); true when it was ended.</summary>
    public static bool End(ProcessEntry entry)
    {
        try
        {
            using var process = Process.GetProcessById(entry.ProcessId);
            if (entry.StartTime is { } started && Math.Abs((process.StartTime.ToUniversalTime() - started).TotalSeconds) > 1) return false;
            process.Kill();
            process.WaitForExit(5000);
            return true;
        }
        catch (Exception error) when (error is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            SdkOutput.WriteLine($"Could not end the JavaScript debug adapter (process {entry.ProcessId}): {error.Message}");
            return false;
        }
    }

    private static List<ProcessEntry>? List()
    {
        try
        {
            var result = new List<ProcessEntry>();
            // Handle is Win32_Process's key: without it the objects have no path, and GetOwnerSid fails.
            using var searcher = new ManagementObjectSearcher("SELECT Handle, ProcessId, ParentProcessId, Name, CommandLine, CreationDate FROM Win32_Process");
            using var objects = searcher.Get();
            foreach (ManagementObject item in objects)
            {
                using (item)
                {
                    string? commandLine = item["CommandLine"] as string;
                    DateTime? started = item["CreationDate"] is string date && date.Length > 0 ? ManagementDateTimeConverter.ToDateTime(date).ToUniversalTime() : null;
                    // Only an adapter's owner matters; GetOwnerSid is one call per process.
                    string? owner = JsDebugAdapters.IsAdapter(commandLine) ? OwnerSid(item) : null;
                    result.Add(new ProcessEntry(Convert.ToInt32(item["ProcessId"]), Convert.ToInt32(item["ParentProcessId"]), item["Name"] as string ?? "", commandLine, started, owner));
                }
            }
            return result;
        }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            // Listing the adapters is a precaution: it never stops an attach.
            SdkOutput.WriteLine("Could not list Visual Studio's JavaScript debug adapters: " + error.Message);
            return null;
        }
    }

    private static string? OwnerSid(ManagementObject process)
    {
        try { return process.InvokeMethod("GetOwnerSid", null, null)?["Sid"] as string; }
        catch (Exception error) when (error is ManagementException or System.Runtime.InteropServices.COMException or UnauthorizedAccessException or InvalidOperationException) { return null; }
    }
}
