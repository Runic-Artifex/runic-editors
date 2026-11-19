using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace RunicArtifex.DevKit.VisualStudio.Core;

/// <summary>One process of a process listing (Windows: <c>Win32_Process</c>).</summary>
public sealed class ProcessEntry
{
    public ProcessEntry(int processId, int parentProcessId, string name, string? commandLine, DateTime? startTime, string? ownerSid = null)
    {
        ProcessId = processId; ParentProcessId = parentProcessId; Name = name; CommandLine = commandLine; StartTime = startTime; OwnerSid = ownerSid;
    }

    public int ProcessId { get; }
    public int ParentProcessId { get; }
    public string Name { get; }
    public string? CommandLine { get; }
    /// <summary>When it started, in UTC; null when unknown.</summary>
    public DateTime? StartTime { get; }
    /// <summary>The SID of its user; null when unknown.</summary>
    public string? OwnerSid { get; }
}

/// <summary>Visual Studio's JavaScript debug adapters in a process listing, by whom they belong to (<see cref="JsDebugAdapters.Census"/>).</summary>
public sealed class AdapterCensus
{
    public AdapterCensus(IReadOnlyList<ProcessEntry> own, IReadOnlyList<ProcessEntry> orphaned, IReadOnlyList<ProcessEntry> unclear) { Own = own; Orphaned = orphaned; Unclear = unclear; }
    /// <summary>The adapters this Visual Studio started.</summary>
    public IReadOnlyList<ProcessEntry> Own { get; }
    /// <summary>Adapters of this user and this Visual Studio installation whose Visual Studio has exited: safe to end.</summary>
    public IReadOnlyList<ProcessEntry> Orphaned { get; }
    /// <summary>Adapters left by an exited process that DevKit does not end: another user's, another installation's, or of unknown owner.</summary>
    public IReadOnlyList<ProcessEntry> Unclear { get; }
}

/// <summary>
/// Visual Studio's JavaScript debug adapter: <c>node.exe …\jsdiagnostics\debugger\debugAdapter\src\vsDebugServer.js</c>,
/// which devenv starts for a JavaScript debug session, including DevKit's page attach.
/// <para>
/// runic-editors#44: when that attach never completes, ending the adapter ends it. Adapters left behind by Visual Studio
/// instances that exited (killed, or even closed normally) outlive them; they were the leading suspect for the hang, but
/// one alone did not cause it in the fix-phase runs. DevKit ends those orphans before an attach when they are provably
/// this user's and this installation's, and ends this Visual Studio's new adapter when an attach does not complete or
/// Visual Studio closes during one.
/// </para>
/// </summary>
public static class JsDebugAdapters
{
    /// <summary>The adapter's script, in its command line.</summary>
    public const string Script = "vsDebugServer.js";

    /// <summary>Whether a command line runs Visual Studio's JavaScript debug adapter.</summary>
    public static bool IsAdapter(string? commandLine)
    {
        if (string.IsNullOrEmpty(commandLine)) return false;
        string normalized = commandLine!.Replace('/', '\\');
        return normalized.IndexOf(Script, StringComparison.OrdinalIgnoreCase) >= 0
            && normalized.IndexOf(@"jsdiagnostics\debugger\debugAdapter", StringComparison.OrdinalIgnoreCase) >= 0;
    }

    /// <summary>
    /// The installation root of a <c>devenv.exe</c> path (<c>…\Microsoft Visual Studio\18\Community</c> for
    /// <c>…\Community\Common7\IDE\devenv.exe</c>); null when the path does not have that shape.
    /// </summary>
    public static string? InstallationRoot(string? devenvPath)
    {
        if (string.IsNullOrEmpty(devenvPath)) return null;
        string normalized = devenvPath!.Replace('/', '\\').TrimEnd('\\');
        const string tail = @"\Common7\IDE\devenv.exe";
        if (!normalized.EndsWith(tail, StringComparison.OrdinalIgnoreCase)) return null;
        string root = normalized.Substring(0, normalized.Length - tail.Length);
        return root.Length == 0 ? null : root;
    }

    /// <summary>Whether the adapter's command line runs a file under the installation root.</summary>
    public static bool BelongsToInstallation(string? commandLine, string? installationRoot)
    {
        if (string.IsNullOrEmpty(commandLine) || string.IsNullOrEmpty(installationRoot)) return false;
        string root = installationRoot!.Replace('/', '\\').TrimEnd('\\') + "\\";
        return commandLine!.Replace('/', '\\').IndexOf(root, StringComparison.OrdinalIgnoreCase) >= 0;
    }

    /// <summary>
    /// Sorts the listing's adapters. One belongs to the Visual Studio <paramref name="devenvId"/> when that process is its
    /// parent or an ancestor. One whose parent has exited (its ID is gone, or reused by a process that started after the
    /// adapter) is orphaned when its owner is <paramref name="userSid"/> and it runs from <paramref name="installationRoot"/>,
    /// and unclear otherwise. An adapter whose parent still runs belongs to that other process and is left out.
    /// </summary>
    public static AdapterCensus Census(IReadOnlyList<ProcessEntry> processes, int devenvId, string? userSid, string? installationRoot)
    {
        var byId = new Dictionary<int, ProcessEntry>();
        foreach (var process in processes) byId[process.ProcessId] = process;
        var own = new List<ProcessEntry>();
        var orphaned = new List<ProcessEntry>();
        var unclear = new List<ProcessEntry>();
        foreach (var adapter in processes.Where(process => IsAdapter(process.CommandLine)))
        {
            if (HasAncestor(adapter, devenvId, byId)) { own.Add(adapter); continue; }
            if (ParentRuns(adapter, byId)) continue;
            bool ours = userSid != null && string.Equals(adapter.OwnerSid, userSid, StringComparison.OrdinalIgnoreCase)
                && BelongsToInstallation(adapter.CommandLine, installationRoot);
            (ours ? orphaned : unclear).Add(adapter);
        }
        return new AdapterCensus(own, orphaned, unclear);
    }

    /// <summary>
    /// The adapters to end when a debugger call hangs: this Visual Studio's adapters whose process IDs were not running
    /// (<paramref name="before"/>) when the call began, or, when none is new (it reused one), all of this Visual Studio's adapters.
    /// </summary>
    public static IReadOnlyList<ProcessEntry> ToEndForHungAttach(IReadOnlyList<ProcessEntry> own, IReadOnlyCollection<int> before)
    {
        var added = own.Where(adapter => !before.Contains(adapter.ProcessId)).ToList();
        return added.Count > 0 ? added : own;
    }

    /// <summary>The Runic SDK pane's line for orphaned adapters DevKit ended.</summary>
    public static string EndedOrphansMessage(IReadOnlyList<ProcessEntry> ended) =>
        $"Ended {Count(ended.Count)} that an exited Visual Studio left running ({Ids(ended)}; runic-editors#44).";

    /// <summary>The Runic SDK pane's warning for leftover adapters DevKit does not end.</summary>
    public static string UnclearMessage(IReadOnlyList<ProcessEntry> unclear) =>
        $"{Capitalized(Count(unclear.Count))} whose Visual Studio has exited {(unclear.Count == 1 ? "is" : "are")} still running ({Ids(unclear)}), but Runic DevKit cannot tell that {(unclear.Count == 1 ? "it belongs" : "they belong")} to you and to this Visual Studio installation, so it leaves {(unclear.Count == 1 ? "it" : "them")} alone. "
        + "If the JavaScript debugger does not attach, end these node.exe processes in Task Manager (see Troubleshooting in the Runic DevKit README).";

    /// <summary>The Runic SDK pane's line when DevKit ends this Visual Studio's adapter of a hung attach.</summary>
    public static string EndedForHungAttachMessage(string reason, IReadOnlyList<ProcessEntry> ended) => ended.Count == 0
        ? $"{reason}: no JavaScript debug adapter of this Visual Studio runs, so Runic DevKit cannot end the attach that is waiting."
        : $"{reason}: ended Visual Studio's JavaScript debug adapter ({Ids(ended)}) so the attach that is waiting ends.";

    private static string Count(int count) => count == 1 ? "1 JavaScript debug adapter (node.exe)" : $"{count} JavaScript debug adapters (node.exe)";
    private static string Capitalized(string text) => text.Length == 0 ? text : char.ToUpperInvariant(text[0]) + text.Substring(1);
    private static string Ids(IReadOnlyList<ProcessEntry> processes) => "process " + string.Join(", ", processes.Select(process => process.ProcessId));

    private static bool ParentRuns(ProcessEntry process, IReadOnlyDictionary<int, ProcessEntry> byId) =>
        process.ParentProcessId > 0 && byId.TryGetValue(process.ParentProcessId, out var parent) && !Reused(parent, process);

    /// <summary>A parent ID now held by a process that started after the child is a reused ID: the real parent has exited.</summary>
    private static bool Reused(ProcessEntry parent, ProcessEntry child) =>
        parent.StartTime is { } parentStart && child.StartTime is { } childStart && parentStart > childStart;

    private static bool HasAncestor(ProcessEntry process, int ancestorId, IReadOnlyDictionary<int, ProcessEntry> byId)
    {
        var current = process;
        for (int depth = 0; depth < 8; depth++)
        {
            if (current.ParentProcessId <= 0 || !byId.TryGetValue(current.ParentProcessId, out var parent) || Reused(parent, current)) return false;
            if (parent.ProcessId == ancestorId) return true;
            current = parent;
        }
        return false;
    }
}
