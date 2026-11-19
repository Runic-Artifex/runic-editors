using System;

namespace RunicArtifex.DevKit.VisualStudio.Core;

/// <summary>What Start dotnet runic dev does when the session file does not show an active <c>dev</c> for the project.</summary>
public enum DevStartStep
{
    /// <summary>No <c>dev</c> this extension started is running: start one.</summary>
    Start,
    /// <summary>The one it started is still stopping: wait for it to exit (<see cref="DevStartGate.StopWait"/>), then start.</summary>
    WaitForStop,
    /// <summary>The one it started runs and was not asked to stop (still starting, or its session file is not written yet): say so.</summary>
    AlreadyRunning,
}

/// <summary>
/// Start dotnet runic dev never does nothing without saying why (the 0.22 checklist, row 6: after Stop, Start stayed
/// enabled but was ignored while the stopped <c>dev</c>'s process lingered).
/// </summary>
public static class DevStartGate
{
    /// <summary>How long Start waits for a stopping <c>dev</c>: its input stop, the forced stop and the kill (<see cref="DevProcess.StopAsync(TimeSpan, TimeSpan)"/>).</summary>
    public static readonly TimeSpan StopWait = TimeSpan.FromSeconds(30);

    public static DevStartStep Decide(bool ownedRunning, bool stopping)
        => !ownedRunning ? DevStartStep.Start : stopping ? DevStartStep.WaitForStop : DevStartStep.AlreadyRunning;

    /// <summary>The Runic SDK pane's line while Start waits.</summary>
    public static string Waiting(string projectName, int processId)
        => $"Waiting for dotnet runic dev (process {processId}) to finish stopping before starting it again for {projectName}.";

    /// <summary>The message when the stopping <c>dev</c> has not exited within <see cref="StopWait"/>.</summary>
    public static string StillStopping(string projectName, int processId)
        => $"dotnet runic dev (process {processId}) for {projectName} has not exited within {StopWait.TotalSeconds:0} seconds of Stop, so it was not started again. Choose Stop dotnet runic dev to end it by force, or end process {processId} in Task Manager, then start it again.";

    /// <summary>The message when the <c>dev</c> this extension started runs and was not asked to stop.</summary>
    public static string AlreadyRunning(string projectName, int processId, string stateLabel)
        => $"dotnet runic dev (process {processId}) that Visual Studio started for {projectName} is still running ({stateLabel}), so another was not started. Wait for it to get ready, or choose Stop dotnet runic dev first.";

    /// <summary>The Runic SDK pane's line when Start finds <c>dev</c> already running in the requested mode.</summary>
    public static string AlreadyActive(string projectName, int? processId, string stateLabel)
        => $"dotnet runic dev{(processId is { } id ? $" (process {id})" : "")} already runs for {projectName} ({stateLabel}); nothing to start.";

    /// <summary>The Runic SDK pane's line when <c>dev</c> exited after a stop and the processes it left running were ended.</summary>
    public static string EndedLeftovers(int count)
        => count == 1
            ? "Ended 1 process that dotnet runic dev left running after it stopped (such as its frontend server)."
            : $"Ended {count} processes that dotnet runic dev left running after it stopped (such as its frontend server and runic-translations serve).";
}
