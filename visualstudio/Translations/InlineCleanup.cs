using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.VisualStudio;
using Microsoft.VisualStudio.LanguageServer.Client;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using RunicArtifex.DevKit.VisualStudio.Core;
using RunicArtifex.DevKit.VisualStudio.Output;
using RunicArtifex.DevKit.VisualStudio.ToolWindow;

namespace RunicArtifex.DevKit.VisualStudio.Translations;

/// <summary>
/// Deletes the child file that Inline Resource empties in Visual Studio once the edit is applied and saved. Visual
/// Studio 2026 does not advertise the <c>delete</c> resource operation, so the language server (Translations 0.14,
/// protocol 2) empties the child instead of deleting it and says so in the action's title ("leaves en.rmf2 empty").
/// This middle layer of <see cref="RunicLanguageClient"/> reads each resolved <c>refactor.inline</c> action and the
/// <c>textDocument/didChange</c> that applies it; after a save, <see cref="InlineEmptiedFiles"/> decides, and the file
/// is deleted through its project item (<c>ProjectItem.Delete</c>), so source control and Solution Explorer follow.
/// Only the exact file the edit emptied is deleted, only when it is saved and empty on disk and the files that received
/// its resources are saved too. A file in no project is left empty, with a note in the Runic Translations pane.
/// </summary>
internal sealed class InlineCleanup : ILanguageClientMiddleLayer2<JToken>, IVsRunningDocTableEvents
{
    private readonly InlineEmptiedFiles tracker = new();
    private bool subscribed;

    public bool CanHandle(string methodName) => methodName is "codeAction/resolve" or "textDocument/didChange";

    public Task HandleNotificationAsync(string methodName, JToken methodParam, Func<JToken, Task> sendNotification)
    {
        try { if (tracker.AwaitsApply && tracker.Changed(methodParam.ToString(Formatting.None), DateTimeOffset.UtcNow)) Log("Inline Resource applied; the emptied file is deleted once it and the edited files are saved."); }
        catch (Exception error) when (error is not OutOfMemoryException) { System.Diagnostics.Trace.WriteLine("Runic: inline cleanup: " + error.Message); }
        return sendNotification(methodParam);
    }

    public async Task<JToken?> HandleRequestAsync(string methodName, JToken methodParam, Func<JToken, Task<JToken?>> sendRequest)
    {
        var result = await sendRequest(methodParam);
        try
        {
            if (result is JObject action && tracker.Offered(action.ToString(Formatting.None), DateTimeOffset.UtcNow))
                _ = RunicSdkState.Background.RunAsync(async () => { await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync(); Subscribe(); });
        }
        catch (Exception error) when (error is not OutOfMemoryException) { System.Diagnostics.Trace.WriteLine("Runic: inline cleanup: " + error.Message); }
        return result;
    }

    private void Subscribe()
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        if (subscribed) return;
        if (ServiceProvider.GlobalProvider.GetService(typeof(SVsRunningDocumentTable)) is IVsRunningDocumentTable table
            && ErrorHandler.Succeeded(table.AdviseRunningDocTableEvents(this, out _))) subscribed = true;
    }

    public int OnAfterSave(uint docCookie)
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        if (ServiceProvider.GlobalProvider.GetService(typeof(SVsRunningDocumentTable)) is not IVsRunningDocumentTable table
            || ErrorHandler.Failed(table.GetDocumentInfo(docCookie, out _, out _, out _, out string moniker, out _, out _, out IntPtr data))) return VSConstants.S_OK;
        if (data != IntPtr.Zero) System.Runtime.InteropServices.Marshal.Release(data);
        if (string.IsNullOrEmpty(moniker) || !tracker.IsFollowed(moniker)) return VSConstants.S_OK;
        var state = new FileState(table);
        foreach (var decision in tracker.AfterSave(moniker, state))
        {
            switch (decision.Outcome)
            {
                case EmptiedFileOutcome.Delete:
                    // Not inside the save notification: the document is closed and removed once the save has finished.
                    string path = decision.Path;
                    _ = RunicSdkState.Background.RunAsync(async () =>
                    {
                        await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync(alwaysYield: true);
                        Delete(path);
                    });
                    break;
                case EmptiedFileOutcome.KeptNotEmpty:
                    Log($"Inline Resource: {decision.Path} is not empty any more; it is kept.");
                    break;
            }
        }
        return VSConstants.S_OK;
    }

    /// <summary>Deletes the emptied file through its project item after checking once more that it is saved and empty.</summary>
    private void Delete(string path)
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        try
        {
            if (ServiceProvider.GlobalProvider.GetService(typeof(SVsRunningDocumentTable)) is not IVsRunningDocumentTable table) return;
            var state = new FileState(table);
            var bytes = state.Read(path, 3);
            if (bytes == null || state.IsDirty(path) || !InlineEmptiedFiles.IsEmpty(bytes))
            {
                Log($"Inline Resource: {path} changed before it could be deleted; it is kept.");
                return;
            }
            var dte = (EnvDTE.DTE)ServiceProvider.GlobalProvider.GetService(typeof(EnvDTE.DTE));
            EnvDTE.ProjectItem? item = dte?.Solution?.FindProjectItem(path);
            if (item == null)
            {
                Log($"Inline Resource left {path} empty. It is in no project of the solution, so delete it yourself.");
                return;
            }
            // Saved and empty: closing its editor loses nothing. ProjectItem.Delete removes it from the project, the
            // disk and source control (IVsTrackProjectDocuments) and closes it if it is still open.
            EnvDTE.Document? document = null;
            try { document = item.Document; } catch (Exception error) when (error is System.Runtime.InteropServices.COMException or InvalidOperationException) { }
            document?.Close(EnvDTE.vsSaveChanges.vsSaveChangesNo);
            item.Delete();
            Log($"Inline Resource: deleted {path}, which it left empty.");
        }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            Log($"Inline Resource left {path} empty and could not delete it: {error.Message}");
        }
    }

    private static void Log(string message) => _ = RunicSdkState.Background.RunAsync(async () =>
    {
        var pane = await RunicOutput.TranslationsAsync();
        await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
        pane.OutputStringThreadSafe(message + Environment.NewLine);
    });

    /// <summary>The Running Document Table's unsaved state and the file on disk.</summary>
    private sealed class FileState : IEmptiedFileState
    {
        private readonly IVsRunningDocumentTable table;
        public FileState(IVsRunningDocumentTable table) { this.table = table; }

        public bool IsDirty(string path)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            if (ErrorHandler.Failed(table.FindAndLockDocument((uint)_VSRDTFLAGS.RDT_NoLock, path, out _, out _, out IntPtr data, out _)) || data == IntPtr.Zero) return false;
            try { return System.Runtime.InteropServices.Marshal.GetObjectForIUnknown(data) is IVsPersistDocData document && ErrorHandler.Succeeded(document.IsDocDataDirty(out int dirty)) && dirty != 0; }
            finally { System.Runtime.InteropServices.Marshal.Release(data); }
        }

        public byte[]? Read(string path, int limit)
        {
            if (!File.Exists(path)) return null;
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            var buffer = new byte[limit + 1];
            int read = 0, count;
            while (read < buffer.Length && (count = stream.Read(buffer, read, buffer.Length - read)) > 0) read += count;
            return buffer.Take(read).ToArray();
        }
    }

    public int OnAfterFirstDocumentLock(uint docCookie, uint dwRDTLockType, uint dwReadLocksRemaining, uint dwEditLocksRemaining) => VSConstants.S_OK;
    public int OnBeforeLastDocumentUnlock(uint docCookie, uint dwRDTLockType, uint dwReadLocksRemaining, uint dwEditLocksRemaining) => VSConstants.S_OK;
    public int OnAfterAttributeChange(uint docCookie, uint grfAttribs) => VSConstants.S_OK;
    public int OnBeforeDocumentWindowShow(uint docCookie, int fFirstShow, IVsWindowFrame pFrame) => VSConstants.S_OK;
    public int OnAfterDocumentWindowHide(uint docCookie, IVsWindowFrame pFrame) => VSConstants.S_OK;
}

/// <summary>Runs a message through each middle layer that handles its method, in order (<see cref="RunicLanguageClient.MiddleLayer"/>).</summary>
internal sealed class MiddleLayers : ILanguageClientMiddleLayer2<JToken>
{
    private readonly ILanguageClientMiddleLayer2<JToken>[] layers;
    public MiddleLayers(params ILanguageClientMiddleLayer2<JToken>[] layers) { this.layers = layers; }

    public bool CanHandle(string methodName) => layers.Any(layer => layer.CanHandle(methodName));

    public Task HandleNotificationAsync(string methodName, JToken methodParam, Func<JToken, Task> sendNotification)
    {
        Func<JToken, Task> next = sendNotification;
        foreach (var layer in Enumerable.Reverse(layers).Where(layer => layer.CanHandle(methodName)))
        {
            var inner = next;
            next = parameter => layer.HandleNotificationAsync(methodName, parameter, inner);
        }
        return next(methodParam);
    }

    public Task<JToken?> HandleRequestAsync(string methodName, JToken methodParam, Func<JToken, Task<JToken?>> sendRequest)
    {
        Func<JToken, Task<JToken?>> next = sendRequest;
        foreach (var layer in Enumerable.Reverse(layers).Where(layer => layer.CanHandle(methodName)))
        {
            var inner = next;
            next = parameter => layer.HandleRequestAsync(methodName, parameter, inner);
        }
        return next(methodParam);
    }
}
