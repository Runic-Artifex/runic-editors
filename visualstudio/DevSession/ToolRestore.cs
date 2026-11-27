using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;
using Microsoft.VisualStudio.Threading;
using RunicArtifex.DevKit.VisualStudio.Core;
using RunicArtifex.DevKit.VisualStudio.ToolWindow;

namespace RunicArtifex.DevKit.VisualStudio.DevSession;

/// <summary>
/// A <c>dotnet runic dev</c> that exited early: the message names its exit code and last lines, and when its output
/// says the local tools are not restored (a fresh clone), offers <c>dotnet tool restore</c> behind a click (D081.4;
/// VS Code: "Restore tools", runic-editors#81). Nothing runs without the click.
/// </summary>
internal static class ToolRestore
{
    private static readonly TimeSpan RestoreTimeout = TimeSpan.FromMinutes(5);

    /// <summary>The message of the last report (the automation test reads it).</summary>
    public static string? LastMessage { get; private set; }

    /// <summary>The exit reported last: a dev that exits before F5's wait begins reaches both reporters, which show it once.</summary>
    private static DevExit? reported;

    public static async Task ReportAsync(RunicPackage package, string project, DevExit exit, CancellationToken cancellationToken)
    {
        await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync(cancellationToken);
        if (ReferenceEquals(reported, exit)) return;
        reported = exit;
        bool offer = exit.ToolsMissing && !VsClosing.IsClosing;
        string message = DevExits.Message(exit, offer);
        LastMessage = message;
        if (!offer) { VsClosing.ShowMessage(package, message, OLEMSGICON.OLEMSGICON_CRITICAL); return; }
        int answer = MessageBoxes.Show(package, message, OLEMSGICON.OLEMSGICON_CRITICAL, OLEMSGBUTTON.OLEMSGBUTTON_YESNO, OLEMSGDEFBUTTON.OLEMSGDEFBUTTON_FIRST);
        if (answer == (int)Microsoft.VisualStudio.VSConstants.MessageBoxResult.IDYES) await RestoreAsync(package, project, cancellationToken);
        else SdkOutput.WriteLine($"Restore tools: not run for {Path.GetFileName(project)}; run dotnet tool restore in {Path.GetDirectoryName(project)} before starting again.");
    }

    /// <summary><c>dotnet tool restore</c> in the project's folder; its output goes to the Runic SDK Output pane.</summary>
    public static async Task RestoreAsync(RunicPackage package, string project, CancellationToken cancellationToken)
    {
        string name = Path.GetFileName(project);
        string directory = Path.GetDirectoryName(project) ?? Environment.CurrentDirectory;
        await SetStatusAsync($"Runic: restoring the local tools of {name}…");
        string message;
        bool passed = false;
        try
        {
            string dotnet = await RunicSdkState.DotnetAsync(cancellationToken);
            await TaskScheduler.Default;
            SdkOutput.WriteLine($"> {dotnet} {string.Join(" ", DevExits.ToolRestoreArguments)} (in {directory})");
            var result = await ToolProcess.RunAsync(dotnet, DevExits.ToolRestoreArguments, directory, RestoreTimeout, cancellationToken);
            foreach (string line in (result.Output + "\n" + result.Error).Split('\n').Select(line => line.TrimEnd()).Where(line => line.Length > 0)) SdkOutput.WriteLine(line);
            passed = result.ExitCode == 0;
            message = passed
                ? $"The local tools of {name} are restored. Press F5 (or Debug the Window) again."
                : $"dotnet tool restore failed (exit code {result.ExitCode}): {result.Detail}. See the Runic SDK Output pane.";
        }
        catch (Exception error) when (error is InvalidOperationException or TimeoutException)
        {
            message = $"dotnet tool restore did not run: {error.Message}";
        }
        SdkOutput.WriteLine("Restore tools: " + message);
        await SetStatusAsync("");
        await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync(cancellationToken);
        VsClosing.ShowMessage(package, message, passed ? OLEMSGICON.OLEMSGICON_INFO : OLEMSGICON.OLEMSGICON_CRITICAL);
    }

    private static Task SetStatusAsync(string text) => Debugging.PageAttach.SetStatusAsync(text);
}
