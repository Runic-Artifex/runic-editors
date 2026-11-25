using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;

namespace RunicArtifex.DevKit.VisualStudio.Core;

/// <summary>
/// <c>dotnet runic dev</c>'s output as VS Code's <c>$runic-dev</c> and <c>$runic-dev-no-window</c> problem matchers read
/// it (<c>vscode/package.json</c> <c>problemPatterns</c>, <c>problemMatchers</c>): MSBuild-form lines become Error List
/// entries, and a new build cycle (<c>[dev] Evaluating</c>, and with the Window <c>[dev] Window exited</c> and
/// <c>[dev] Reloading</c>) replaces the previous cycle's entries, as a background matcher does.
/// </summary>
public static class DevOutput
{
    /// <summary>The <c>$runic-dev</c> pattern (runic-sdk <c>tools/dotnet-runic/README.md</c>, "Diagnostics").</summary>
    public static readonly Regex Diagnostic = new(
        @"^\s*(?:\d+>)?(?<file>\S.*?)(?:\((?<line>\d+)(?:,(?<column>\d+))?(?:,\d+,\d+)?\))?\s*:\s+(?:\S+\s+)?(?<severity>error|warning)\s+(?<code>[A-Za-z]+\d+)\s*:\s*(?<message>.*?)(?:\s+\[[^\]]+\])?$",
        RegexOptions.CultureInvariant);

    private static readonly Regex BeginsWithWindow = new(@"^\[dev\] (Evaluating|Window exited|Reloading)", RegexOptions.CultureInvariant);
    private static readonly Regex BeginsNoWindow = new(@"^\[dev\] Evaluating", RegexOptions.CultureInvariant);
    private static readonly Regex Ends = new(@"^\[dev\] Ready$", RegexOptions.CultureInvariant);
    private static readonly Regex FrontendCleared = new(@"^\[vite\] \[runic\] Frontend errors cleared$", RegexOptions.CultureInvariant);

    /// <summary>
    /// The code of a frontend (Vite) error that <c>dev</c> prints as an MSBuild-form line after its <c>[vite] </c> label
    /// (runic-sdk 0.23, runic-sdk#1212): <c>[vite] /…/Frontend/src/App.tsx(29,50): error RUNICP008: [vite:oxc] …</c>.
    /// </summary>
    public const string FrontendErrorCode = "RUNICP008";
    private static readonly Regex Escape = new(@"\x1B\[[0-9;?]*[ -/]*[@-~]", RegexOptions.CultureInvariant);
    /// <summary>
    /// The label <c>dev</c> puts before each line of a process it runs (<c>[host] </c>, <c>[app] </c>, <c>[vite] </c>;
    /// runic-sdk <c>RunningProcess</c>). Without it, a build line's file would read <c>[host] C:\…\App.cs</c>.
    /// </summary>
    private static readonly Regex ProcessLabel = new(@"^\[[A-Za-z][\w-]*\] ", RegexOptions.CultureInvariant);

    /// <summary>
    /// The arguments of <c>dotnet runic dev</c> for a project, as VS Code's task runs it. <paramref name="debugEndpoint"/>
    /// adds <c>--debug-endpoint</c> (0.22) to a <c>dev</c> with its Window, so a CS-WebUI Window opens its DevTools
    /// Protocol port because an IDE asked (D070.17); <c>--no-window</c> needs nothing. <paramref name="verb"/> is the
    /// project's core tool (<c>runic</c> or <c>rax</c>; default: by its tool manifest, <see cref="RunicTools.VerbFor"/>).
    /// </summary>
    public static IReadOnlyList<string> Arguments(string project, bool noWindow, bool debugEndpoint = false, string? verb = null)
    {
        verb ??= RunicTools.VerbFor(project);
        return noWindow ? new[] { verb, "dev", "--no-window", "--project", project }
            : debugEndpoint ? new[] { verb, "dev", "--debug-endpoint", "--project", project } : new[] { verb, "dev", "--project", project };
    }

    /// <summary>The line without terminal colour codes.</summary>
    public static string Plain(string line) => Escape.Replace(line, "");

    /// <summary>
    /// The Error List entry of one output line, or null. A relative file resolves against <paramref name="directory"/>
    /// (<c>dev</c>'s working directory, the project's), as the matcher's <c>autoDetect</c> does. Runic codes link to their
    /// entry in the diagnostics catalog at the project's tool version.
    /// </summary>
    public static RunicErrorEntry? Parse(string line, string directory, string? toolVersion = null)
    {
        var match = Diagnostic.Match(ProcessLabel.Replace(Plain(line), "", 1));
        if (!match.Success) return null;
        string file = match.Groups["file"].Value.Trim();
        try { if (!Path.IsPathRooted(file)) file = Path.GetFullPath(Path.Combine(directory, file)); }
        catch (Exception error) when (error is ArgumentException or NotSupportedException or PathTooLongException) { }
        int lineNumber = match.Groups["line"].Success ? int.Parse(match.Groups["line"].Value) : 1;
        int column = match.Groups["column"].Success ? int.Parse(match.Groups["column"].Value) : 1;
        string code = match.Groups["code"].Value;
        return new RunicErrorEntry(
            match.Groups["severity"].Value == "error" ? RunicErrorSeverity.Error : RunicErrorSeverity.Warning, code,
            $"{code}: {match.Groups["message"].Value}", file, Math.Max(0, lineNumber - 1), Math.Max(0, column - 1),
            RunicHelpLinks.IsRunicCode(code) ? RunicHelpLinks.Diagnostic(code, toolVersion) : "");
    }

    /// <summary>Whether the line starts a new build cycle of the matcher for this mode.</summary>
    public static bool BeginsCycle(string line, bool noWindow) => (noWindow ? BeginsNoWindow : BeginsWithWindow).IsMatch(Plain(line));

    /// <summary><c>[dev] Ready</c>.</summary>
    public static bool IsReady(string line) => Ends.IsMatch(Plain(line).TrimEnd());

    /// <summary>
    /// <c>[vite] [runic] Frontend errors cleared</c>: each module that failed compiles again, so the
    /// <see cref="FrontendErrorCode"/> rows go (runic-sdk 0.23).
    /// </summary>
    public static bool IsFrontendErrorsCleared(string line) => FrontendCleared.IsMatch(Plain(line).TrimEnd());

    /// <summary>Whether the entry is a frontend (Vite) error, which only <see cref="IsFrontendErrorsCleared"/> or a new cycle removes.</summary>
    public static bool IsFrontendError(RunicErrorEntry entry) => string.Equals(entry.Code, FrontendErrorCode, StringComparison.OrdinalIgnoreCase);
}

/// <summary>
/// The Error List entries of one running <c>dotnet runic dev</c>: <see cref="Add"/> each output line (standard output
/// and error); it returns true when the entries changed. Not thread-safe; the caller serializes lines.
/// <para>
/// <c>[dev] Ready</c> also drops the build errors read before it: <c>dev</c> is ready only after a build that succeeded,
/// so they belong to a build that the fix replaced (after a failed build <c>dotnet watch</c> waits for a change, rebuilds
/// and starts the Window again without a new <c>Window exited</c>). Warnings stay until the next cycle. Frontend errors
/// (<see cref="DevOutput.FrontendErrorCode"/>, after the <c>[vite] </c> label) stay until
/// <c>[vite] [runic] Frontend errors cleared</c> or the next cycle (runic-editors#52).
/// </para>
/// </summary>
public sealed class DevProblems
{
    private readonly string directory;
    private readonly bool noWindow;
    private readonly string? toolVersion;
    private readonly List<RunicErrorEntry> entries = new();
    private readonly HashSet<string> seen = new(StringComparer.Ordinal);

    public DevProblems(string project, bool noWindow, string? toolVersion = null)
    {
        directory = Path.GetDirectoryName(project) ?? "";
        this.noWindow = noWindow; this.toolVersion = toolVersion;
    }

    public IReadOnlyList<RunicErrorEntry> Entries => entries.ToList();

    public bool Add(string line)
    {
        if (DevOutput.BeginsCycle(line, noWindow))
        {
            if (entries.Count == 0) return false;
            entries.Clear(); seen.Clear();
            return true;
        }
        // A .NET build that succeeded fixes the build errors, not the frontend's.
        if (DevOutput.IsReady(line)) return Remove(entry => entry.Severity == RunicErrorSeverity.Error && !DevOutput.IsFrontendError(entry));
        if (DevOutput.IsFrontendErrorsCleared(line)) return Remove(DevOutput.IsFrontendError);
        if (DevOutput.Parse(line, directory, toolVersion) is not { } entry) return false;
        // MSBuild repeats a build's diagnostics in its summary; one entry each.
        if (!seen.Add(Key(entry))) return false;
        entries.Add(entry);
        return true;
    }

    private bool Remove(Predicate<RunicErrorEntry> match)
    {
        if (entries.RemoveAll(match) == 0) return false;
        seen.Clear();
        foreach (var kept in entries) seen.Add(Key(kept));
        return true;
    }

        private static string Key(RunicErrorEntry entry) => $"{entry.Severity}|{entry.File}|{entry.Line}|{entry.Column}|{entry.Text}";
}

/// <summary>What the pre-F5 guard does when F5 (or Ctrl+F5) starts a Runic startup project (D070.6).</summary>
public enum DevGuardDecision
{
    /// <summary><c>dev --no-window</c> is ready with owner <c>ide</c>: launch.</summary>
    Launch,
    /// <summary><c>dev --no-window</c> is starting: launch, its frontend follows (VS Code waits; the IDE cannot block a build that long).</summary>
    LaunchWhileStarting,
    /// <summary>No <c>dev</c> runs: ask whether to start <c>dev --no-window</c> or continue without it.</summary>
    AskToStart,
    /// <summary><c>dev</c> runs its own Window: ask whether to restart it with <c>--no-window</c> or continue without.</summary>
    AskToRestart,
}

/// <summary>
/// The pre-F5 guard (PARITY §2.2, D070.6): Visual Studio has no <c>preLaunchTask</c>, so when F5 or Ctrl+F5 starts a
/// Runic startup project and no <c>dotnet runic dev --no-window</c> (owner <c>ide</c>) runs, the extension asks first.
/// VS Code's <c>runic</c> debug type starts <c>dev --no-window</c> without asking and remembers nothing, so the guard
/// asks each time (no per-solution choice).
/// </summary>
public static class DevGuard
{
    /// <summary>The command set of Debug.Start and Debug.StartWithoutDebugging (Visual Studio's guidVSStd97).</summary>
    public static readonly Guid LaunchCommandSet = new("5efc7975-14bc-11cf-9b2b-00aa00573819");
    /// <summary>Debug.Start (F5; cmdidStart). In break mode the same command continues.</summary>
    public const uint StartCommand = 295;
    /// <summary>Debug.StartWithoutDebugging (Ctrl+F5; cmdidStartNoDebug).</summary>
    public const uint StartWithoutDebuggingCommand = 368;

    /// <summary>
    /// Which launch a command is: true for Debug.Start, false for Debug.StartWithoutDebugging, null for any other
    /// command, which the guard passes on.
    /// </summary>
    public static bool? LaunchKind(Guid commandSet, uint command)
    {
        if (commandSet != LaunchCommandSet) return null;
        return command switch
        {
            StartCommand => true,
            StartWithoutDebuggingCommand => false,
            _ => null,
        };
    }

    /// <summary>
    /// The project the guard asks about when a launch command runs: the first startup project (in the solution's
    /// startup order) that is a Runic SDK project, as <paramref name="sdkProjects"/> spells it (the dev session's key). Null while the debugger is not in design mode (F5 then continues a
    /// running session) and when no startup project is a Runic SDK project.
    /// </summary>
    public static string? Target(IReadOnlyList<string> startupProjects, IReadOnlyList<string> sdkProjects, bool debuggerInDesignMode)
    {
        if (!debuggerInDesignMode) return null;
        foreach (string startup in startupProjects)
            if (sdkProjects.FirstOrDefault(project => string.Equals(project, startup, StringComparison.OrdinalIgnoreCase)) is { } match) return match;
        return null;
    }

    public static DevGuardDecision Decide(DevSnapshot snapshot)
    {
        if (snapshot.IsActive && snapshot.Session?.Window?.OwnedByIde == true)
            return snapshot.State == DevState.Ready ? DevGuardDecision.Launch : DevGuardDecision.LaunchWhileStarting;
        return snapshot.IsActive ? DevGuardDecision.AskToRestart : DevGuardDecision.AskToStart;
    }

    /// <summary>The question; Yes starts (or restarts with) <c>--no-window</c>, No continues without, Cancel cancels the launch.</summary>
    public static string Question(string project, DevGuardDecision decision, bool startedHere)
    {
        string name = Path.GetFileName(project);
        return decision switch
        {
            DevGuardDecision.AskToStart =>
                $"dotnet runic dev --no-window does not run for {name}. It serves the frontend that the Window you are starting loads in Development.\n\n"
                + "Yes: start dotnet runic dev --no-window, wait until it is ready, then start the Window.\n"
                + "No: continue without it.\nCancel: do not start.",
            DevGuardDecision.AskToRestart when startedHere =>
                $"dotnet runic dev runs {name}'s Window itself. Restart it with --no-window so that Visual Studio starts the Window?\n\n"
                + "Yes: restart it with --no-window, wait until it is ready, then start the Window.\n"
                + "No: continue without it (two Windows may run).\nCancel: do not start.",
            DevGuardDecision.AskToRestart =>
                $"dotnet runic dev runs {name}'s Window itself and was started outside Visual Studio. Stop it with Ctrl+C in its terminal and start the Window again to use --no-window.\n\n"
                + "Yes or No: continue anyway (two Windows may run).\nCancel: do not start.",
            _ => "",
        };
    }
}

/// <summary>Which Runic SDK project a dev command without a target acts on.</summary>
public static class DevTargets
{
    public const string Ambiguous = "This solution has several Runic SDK projects. Open a file of the one to use, or make it the startup project.";

    /// <summary>
    /// The only project; with several, the one containing the active document, else the startup project when it is one
    /// (Visual Studio has no quick pick; VS Code asks). Throws <see cref="InvalidOperationException"/> when none fits.
    /// </summary>
    public static string Pick(IReadOnlyList<string> projects, string? activeDocument, IReadOnlyList<string>? startupProjects)
    {
        if (projects.Count == 0) throw new InvalidOperationException(SdkSections.NoSdkProject);
        if (projects.Count == 1) return projects[0];
        if (!string.IsNullOrEmpty(activeDocument))
        {
            var containing = projects.Where(project => IsUnder(activeDocument!, Path.GetDirectoryName(project) ?? "")).ToList();
            if (containing.Count == 1) return containing[0];
            // Nested projects: the deepest directory wins.
            if (containing.Count > 1) return containing.OrderByDescending(project => project.Length).First();
        }
        var startup = projects.Where(project => startupProjects?.Contains(project, StringComparer.OrdinalIgnoreCase) == true).ToList();
        if (startup.Count == 1) return startup[0];
        throw new InvalidOperationException(Ambiguous);
    }

    private static bool IsUnder(string file, string directory)
    {
        if (directory.Length == 0) return false;
        string prefix = directory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
        try { return Path.GetFullPath(file).StartsWith(Path.GetFullPath(prefix), StringComparison.OrdinalIgnoreCase); }
        catch (Exception error) when (error is ArgumentException or NotSupportedException or PathTooLongException) { return false; }
    }
}
