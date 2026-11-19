using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace RunicArtifex.DevKit.VisualStudio.Core;

/// <summary>
/// Off Windows, the processes under the <c>dotnet</c> launcher of <c>dotnet runic dev</c> (runic-editors#53). The launcher
/// runs <c>dev</c> as its child and forwards no SIGINT, and a SIGKILL of the launcher alone leaves <c>dev</c>, its
/// frontend server and its Window running. <see cref="DevProcess"/>'s forced stop therefore signals the whole tree, as
/// Ctrl+C in a terminal signals its process group. On Windows the Job object (<see cref="DevProcessTree"/>) holds them.
/// </summary>
public static class PosixProcessTree
{
    /// <summary>Parent IDs from <c>ps -A -o pid=,ppid=</c>'s output: one "pid ppid" pair per line.</summary>
    public static IReadOnlyDictionary<int, int> ParseParents(string psOutput)
    {
        var parents = new Dictionary<int, int>();
        foreach (string line in psOutput.Split('\n'))
        {
            var parts = line.Split(new[] { ' ', '\t', '\r' }, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length >= 2 && int.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out int pid)
                && int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out int parent))
                parents[pid] = parent;
        }
        return parents;
    }

    /// <summary><paramref name="root"/> and its descendants in <paramref name="parents"/>, the root first, parents before their children.</summary>
    public static IReadOnlyList<int> Tree(int root, IReadOnlyDictionary<int, int> parents)
    {
        var children = parents.Where(pair => pair.Key != pair.Value).ToLookup(pair => pair.Value, pair => pair.Key);
        var result = new List<int> { root };
        var seen = new HashSet<int> { root };
        for (int index = 0; index < result.Count; index++)
            foreach (int child in children[result[index]].OrderBy(id => id))
                if (seen.Add(child)) result.Add(child);
        return result;
    }

    /// <summary>The running tree under <paramref name="root"/>; the root alone when <c>ps</c> fails.</summary>
    public static IReadOnlyList<int> Of(int root)
    {
        try
        {
            using var ps = Process.Start(new ProcessStartInfo("ps", "-A -o pid=,ppid=") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true });
            if (ps == null) return new[] { root };
            string output = ps.StandardOutput.ReadToEnd();
            ps.WaitForExit(5000);
            return Tree(root, ParseParents(output));
        }
        catch (Exception error) when (error is InvalidOperationException or System.ComponentModel.Win32Exception) { return new[] { root }; }
    }

    /// <summary>Sends <paramref name="signal"/> (<c>INT</c>, <c>KILL</c>) to each process with <c>kill</c>; one that has exited is skipped.</summary>
    public static void Signal(string signal, IEnumerable<int> processes)
    {
        var ids = processes.Where(id => id > 0).Select(id => id.ToString(CultureInfo.InvariantCulture)).ToList();
        if (ids.Count == 0) return;
        try
        {
            using var kill = Process.Start(new ProcessStartInfo("kill", $"-{signal} {string.Join(" ", ids)}") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true });
            kill?.WaitForExit(5000);
        }
        catch (Exception error) when (error is InvalidOperationException or System.ComponentModel.Win32Exception) { }
    }

    /// <summary>Whether the process still runs.</summary>
    public static bool IsRunning(int processId)
    {
        try { using var process = Process.GetProcessById(processId); return !process.HasExited; }
        catch (ArgumentException) { return false; }
        catch (InvalidOperationException) { return false; }
    }

    /// <summary>Waits until none of <paramref name="processes"/> runs, or <paramref name="timeout"/> has passed; returns those still running.</summary>
    public static async Task<IReadOnlyList<int>> WaitForExitAsync(IReadOnlyList<int> processes, TimeSpan timeout, CancellationToken cancellationToken = default)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (true)
        {
            var running = processes.Where(IsRunning).ToList();
            if (running.Count == 0 || DateTime.UtcNow >= deadline) return running;
            await Task.Delay(100, cancellationToken).ConfigureAwait(false);
        }
    }
}
