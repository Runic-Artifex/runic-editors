using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.VisualStudio;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;
using Microsoft.VisualStudio.Threading;
using RunicArtifex.DevKit.VisualStudio.Core;
using RunicArtifex.DevKit.VisualStudio.ToolWindow;
using SolutionEvents = Microsoft.VisualStudio.Shell.Events.SolutionEvents;

namespace RunicArtifex.DevKit.VisualStudio.Translations;

/// <summary>
/// <c>runic-translations status</c> of each runic.json project of the solution, for the Translations section and Show
/// Translation Status (VS Code: <c>extension.ts</c> <c>projectStatus</c>). The command reads the files on disk, so the
/// status is read again after a Runic file is saved, after an import and on Refresh Translation Status. A catalog that
/// does not compile has no report until it compiles again.
/// </summary>
internal sealed class TranslationStatusState : IVsRunningDocTableEvents
{
    public static TranslationStatusState Instance { get; } = new();

    private readonly object gate = new();
    private IReadOnlyList<TranslationsProjectState> projects = Array.Empty<TranslationsProjectState>();
    private Timer? refreshTimer;
    private bool subscribed;
    private bool scanned;

    private TranslationStatusState() { }

    /// <summary>Raised on any thread when the projects or their status change.</summary>
    public event EventHandler? Changed;

    public IReadOnlyList<TranslationsProjectState> Projects { get { lock (gate) return projects; } }

    /// <summary>True once the solution has been searched for runic.json projects.</summary>
    public bool Scanned => scanned;

    /// <summary>The solution directory of the last scan, for project labels.</summary>
    public string? SolutionDirectory { get; private set; }

    /// <summary>Finds the projects and reads their status once.</summary>
    public async Task EnsureLoadedAsync(CancellationToken cancellationToken)
    {
        if (!scanned) await RefreshAsync(cancellationToken);
    }

    /// <summary>Refresh Translation Status: finds the solution's runic.json projects again and reads each one's status.</summary>
    public async Task RefreshAsync(CancellationToken cancellationToken)
    {
        await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync(cancellationToken);
        Subscribe();
        SolutionDirectory = await TranslationsWorkspace.SolutionDirectoryAsync(cancellationToken);
        var found = await TranslationsWorkspace.ProjectsAsync(cancellationToken);
        lock (gate)
        {
            var previous = projects.ToDictionary(state => state.Manifest, StringComparer.OrdinalIgnoreCase);
            projects = found.Select(manifest => previous.TryGetValue(manifest, out var state) ? state : new TranslationsProjectState(manifest)).ToList();
        }
        scanned = true;
        Raise();
        await Task.WhenAll(Projects.Select(state => ReadAsync(state, cancellationToken)));
    }

    /// <summary>Stores a report that Show Translation Status read, so the section shows the same numbers.</summary>
    public void Store(string manifest, StatusResult result)
    {
        lock (gate)
        {
            var state = projects.FirstOrDefault(project => string.Equals(project.Manifest, manifest, StringComparison.OrdinalIgnoreCase));
            if (state == null) projects = projects.Concat(new[] { state = new TranslationsProjectState(manifest) }).OrderBy(project => project.Manifest, StringComparer.Ordinal).ToList();
            state.Result = result; state.Error = null; state.Loaded = true;
        }
        Raise();
    }

    /// <summary>Reads every project's status again shortly (a save, an import), once the section has loaded.</summary>
    public void Invalidate()
    {
        if (!scanned) return;
        lock (gate)
        {
            refreshTimer?.Dispose();
            refreshTimer = new Timer(_ => _ = RunicSdkState.Background.RunAsync(async () =>
            {
                try { await Task.WhenAll(Projects.Select(state => ReadAsync(state, CancellationToken.None))); }
                catch (Exception error) when (error is not OutOfMemoryException) { System.Diagnostics.Trace.WriteLine("Runic: translation status: " + error.Message); }
            }), null, 500, Timeout.Infinite);
        }
    }

    private async Task ReadAsync(TranslationsProjectState state, CancellationToken cancellationToken)
    {
        lock (gate) { if (state.Running) return; state.Running = true; }
        Raise();
        try
        {
            string stdout = await TranslationsWorkspace.RunToolAsync(state.Manifest, TranslationsTool.StatusArguments(state.Manifest), cancellationToken, quiet: true);
            state.Result = TranslationStatus.Parse(stdout); state.Error = null;
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            // As VS Code's view: a failed run (an unrestored tool, a catalog that does not compile) has no report.
            state.Result = null;
            state.Error = error.Message.Split(new[] { '\r', '\n' }, 2)[0];
        }
        finally { state.Running = false; state.Loaded = true; Raise(); }
    }

    private void Subscribe()
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        if (subscribed) return;
        subscribed = true;
        SolutionEvents.OnAfterOpenSolution += (_, _) => Rescan();
        SolutionEvents.OnAfterBackgroundSolutionLoadComplete += (_, _) => Rescan();
        SolutionEvents.OnAfterCloseSolution += (_, _) => Rescan();
        if (ServiceProvider.GlobalProvider.GetService(typeof(SVsRunningDocumentTable)) is IVsRunningDocumentTable table)
            table.AdviseRunningDocTableEvents(this, out _);
    }

    private void Rescan() => _ = RunicSdkState.Background.RunAsync(async () =>
    {
        try { await RefreshAsync(CancellationToken.None); }
        catch (Exception error) when (error is not OutOfMemoryException) { System.Diagnostics.Trace.WriteLine("Runic: translation status: " + error.Message); }
    });

    /// <summary>A saved MF2 source, runic.json or review file changes the status (VS Code's <c>onDidSaveTextDocument</c>).</summary>
    public int OnAfterSave(uint docCookie)
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        if (ServiceProvider.GlobalProvider.GetService(typeof(SVsRunningDocumentTable)) is IVsRunningDocumentTable table
            && ErrorHandler.Succeeded(table.GetDocumentInfo(docCookie, out _, out _, out _, out string moniker, out _, out _, out IntPtr data)))
        {
            if (data != IntPtr.Zero) System.Runtime.InteropServices.Marshal.Release(data);
            if (IsRunicFile(moniker)) Invalidate();
        }
        return VSConstants.S_OK;
    }

    internal static bool IsRunicFile(string? path)
    {
        if (string.IsNullOrEmpty(path)) return false;
        string name = Path.GetFileName(path);
        return name.Equals("runic.json", StringComparison.OrdinalIgnoreCase) || name.EndsWith(".review.json", StringComparison.OrdinalIgnoreCase)
            || name.EndsWith(".rmf2", StringComparison.OrdinalIgnoreCase) || name.EndsWith(".mf2", StringComparison.OrdinalIgnoreCase);
    }

    public int OnAfterFirstDocumentLock(uint docCookie, uint dwRDTLockType, uint dwReadLocksRemaining, uint dwEditLocksRemaining) => VSConstants.S_OK;
    public int OnBeforeLastDocumentUnlock(uint docCookie, uint dwRDTLockType, uint dwReadLocksRemaining, uint dwEditLocksRemaining) => VSConstants.S_OK;
    public int OnAfterAttributeChange(uint docCookie, uint grfAttribs) => VSConstants.S_OK;
    public int OnBeforeDocumentWindowShow(uint docCookie, int fFirstShow, IVsWindowFrame pFrame) => VSConstants.S_OK;
    public int OnAfterDocumentWindowHide(uint docCookie, IVsWindowFrame pFrame) => VSConstants.S_OK;

    private void Raise() => Changed?.Invoke(this, EventArgs.Empty);
}
