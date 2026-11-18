using System;
using System.Threading.Tasks;
using Microsoft.VisualStudio.LanguageServer.Client;
using Newtonsoft.Json.Linq;
using RunicArtifex.DevKit.VisualStudio.Core;
using RunicArtifex.DevKit.VisualStudio.ToolWindow;
using StreamJsonRpc;

namespace RunicArtifex.DevKit.VisualStudio.Translations;

/// <summary>
/// Shows why the language server refused F2 (runic-editors#32). The server answers a rename it cannot carry out with a
/// JSON-RPC error and its reason (RequestFailed in protocol 2); Visual Studio's language client passes the error to
/// this middle layer and then closes the rename box without a message. The layer reports the refusal that
/// <see cref="RenameRefusal"/> decides on through <see cref="RefusalReport"/> (an InfoBar, the status bar and the Runic
/// Translations Output pane), and lets the error continue to Visual Studio unchanged.
/// </summary>
internal sealed class RenameRefusals : ILanguageClientMiddleLayer2<JToken>
{
    private readonly RefusalReport report;
    public RenameRefusals(RefusalReport report) { this.report = report; }

    public bool CanHandle(string methodName) => RenameRefusal.Follows(methodName);

    public Task HandleNotificationAsync(string methodName, JToken methodParam, Func<JToken, Task> sendNotification) => sendNotification(methodParam);

    public async Task<JToken?> HandleRequestAsync(string methodName, JToken methodParam, Func<JToken, Task<JToken?>> sendRequest)
    {
        try { return await sendRequest(methodParam); }
        catch (Exception error) when (Describe(methodName, error) is { } message)
        {
            _ = RunicSdkState.Background.RunAsync(() => report.ReportAsync(message));
            throw;
        }
    }

    /// <summary>The message for the error, from the server's code and reason (the innermost RemoteInvocationException).</summary>
    private static string? Describe(string method, Exception error)
    {
        try
        {
            bool cancelled = false;
            for (Exception? current = error; current != null; current = current.InnerException)
            {
                if (current is RemoteInvocationException remote) return RenameRefusal.Message(method, remote.ErrorCode, remote.Message);
                if (current is OperationCanceledException) cancelled = true;
            }
            return RenameRefusal.Message(method, null, error.Message, cancelled);
        }
        catch (Exception failure) when (failure is not OutOfMemoryException)
        {
            System.Diagnostics.Trace.WriteLine("Runic: rename refusal: " + failure.Message);
            return null;
        }
    }
}
