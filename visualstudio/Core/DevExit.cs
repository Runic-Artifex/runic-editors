using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace RunicArtifex.DevKit.VisualStudio.Core;

/// <summary>
/// How a <c>dotnet runic dev</c> that this extension started ended: its exit code, its last output lines, and how many
/// Error List rows its output gave (VS Code: <c>DevExit</c> in <c>sdk.ts</c>).
/// </summary>
public sealed class DevExit
{
    public DevExit(int code, IReadOnlyList<string> lines, int problems) { Code = code; Lines = lines; Problems = problems; }

    public int Code { get; }
    /// <summary>The last non-empty output lines, oldest first (at most <see cref="DevExits.KeptLines"/>).</summary>
    public IReadOnlyList<string> Lines { get; }
    /// <summary>The Error List rows that <c>dev</c>'s output gave; 0 means the Error List has nothing of it.</summary>
    public int Problems { get; }
    /// <summary>Whether the output says the project's local tools are not restored (a fresh clone).</summary>
    public bool ToolsMissing => DevExits.ToolsMissing(Lines);
}

/// <summary>
/// The message of a <c>dev</c> that exited before its session was ready, and the restore DevKit offers behind a click
/// (D081.4), as VS Code's <c>devExitMessage</c>, <c>toolsMissing</c> and <c>toolRestoreArguments</c>
/// (<c>vscode/src/dev-process.ts</c>).
/// </summary>
public static class DevExits
{
    /// <summary>How many output lines a <c>dev</c> keeps for the message.</summary>
    public const int KeptLines = 20;
    /// <summary>How many of them the message shows.</summary>
    public const int MessageLines = 3;

    /// <summary>
    /// <c>dotnet</c>'s message for a local tool of the manifest that is not restored, in any language: English
    /// <c>Run "dotnet tool restore" to make the … command available.</c>, German <c>Führen Sie "dotnet tool restore" aus …</c>.
    /// </summary>
    private static readonly Regex ToolRestore = new("dotnet tool restore", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    /// <summary>The service's own line after the exit, which the message states already.</summary>
    private static readonly Regex OwnExitLine = new("^dotnet (runic|rax) dev exited with ", RegexOptions.CultureInvariant);

    public static bool ToolsMissing(IEnumerable<string> lines) => lines.Any(line => ToolRestore.IsMatch(line));

    /// <summary>The arguments of the restore DevKit offers: <c>dotnet tool restore</c> in the project's folder.</summary>
    public static IReadOnlyList<string> ToolRestoreArguments { get; } = new[] { "tool", "restore" };

    /// <summary>
    /// The message: the exit code, the last <see cref="MessageLines"/> output lines, the restore it needs when the tools
    /// are missing, and where the rest is: the Error List only when <c>dev</c>'s output put rows there, else the pane.
    /// </summary>
    public static string Message(DevExit exit, bool offerRestore)
    {
        var kept = exit.Lines.Select(line => line.Trim()).Where(line => line.Length > 0 && !OwnExitLine.IsMatch(line)).ToList();
        var last = kept.Skip(Math.Max(0, kept.Count - MessageLines)).ToList();
        string sentence = $"dotnet runic dev exited with code {exit.Code} before its session was ready{(last.Count > 0 ? ": " + string.Join(" | ", last) : "")}";
        if (!sentence.EndsWith(".", StringComparison.Ordinal) && !sentence.EndsWith("!", StringComparison.Ordinal) && !sentence.EndsWith("?", StringComparison.Ordinal)) sentence += ".";
        string where = exit.Problems > 0 ? " See the Error List and the Runic SDK Output pane." : " Its full output is in the Runic SDK Output pane.";
        string fix = !exit.ToolsMissing ? ""
            : offerRestore ? "\n\nThe project's local tools are not restored. Restore them now (dotnet tool restore)? Then start again."
            : " The project's local tools are not restored: run dotnet tool restore in the project's folder, then start again.";
        return sentence + where + fix;
    }

    /// <summary>The message when <c>dev</c> stopped and DevKit has no exit of its own to report (one started elsewhere).</summary>
    public const string StoppedElsewhere = "dotnet runic dev stopped before its session was ready; see the Runic SDK Output pane.";

    /// <summary>Keeps the last <see cref="KeptLines"/> non-empty lines of a running <c>dev</c>; thread-safe.</summary>
    public sealed class LastLines
    {
        private readonly Queue<string> lines = new();

        public void Add(string line)
        {
            if (string.IsNullOrWhiteSpace(line)) return;
            lock (lines)
            {
                lines.Enqueue(line.TrimEnd());
                while (lines.Count > KeptLines) lines.Dequeue();
            }
        }

        public IReadOnlyList<string> Snapshot() { lock (lines) return lines.ToList(); }
    }
}

/// <summary>The <c>dev</c> that F5 or Debug the Window started exited before its session was ready.</summary>
public sealed class DevExitException : InvalidOperationException
{
    public DevExitException(string project, DevExit exit) : base(DevExits.Message(exit, offerRestore: false)) { Project = project; Exit = exit; }

    public string Project { get; }
    public DevExit Exit { get; }
}
