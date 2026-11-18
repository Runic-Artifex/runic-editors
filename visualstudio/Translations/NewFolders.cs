using System;
using System.IO;
using System.Threading.Tasks;
using Microsoft.VisualStudio.LanguageServer.Client;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using RunicArtifex.DevKit.VisualStudio.Core;
using RunicArtifex.DevKit.VisualStudio.ToolWindow;

namespace RunicArtifex.DevKit.VisualStudio.Translations;

/// <summary>
/// Creates the missing folder of a file a Translations refactoring creates, before Visual Studio applies the edit.
/// Extract Group into a new folder (<c>counter/en.rmf2</c> beside a missing <c>counter/</c>) answers with an edit of the
/// source, a <c>create</c> and an edit of the new file; Visual Studio 18 applies the first and silently drops the other
/// two when the folder does not exist, so the group's messages are lost at the next save. This middle layer reads each
/// resolved code action (and <c>workspace/applyEdit</c> parameters, should Visual Studio route them through middle
/// layers) and acts on <see cref="NewFileFolders.Plan"/>: it creates the folders inside the solution or the Translations
/// project, or, when it may not or cannot, removes the edit so Visual Studio applies none of it and reports why through
/// <see cref="RefusalReport"/>.
/// </summary>
internal sealed class NewFolders : ILanguageClientMiddleLayer2<JToken>
{
    private readonly Func<string?> solutionDirectory;
    private readonly RefusalReport report;

    public NewFolders(Func<string?> solutionDirectory, RefusalReport report) { this.solutionDirectory = solutionDirectory; this.report = report; }

    public bool CanHandle(string methodName) => NewFileFolders.Follows(methodName);

    public Task HandleNotificationAsync(string methodName, JToken methodParam, Func<JToken, Task> sendNotification) => sendNotification(methodParam);

    public async Task<JToken?> HandleRequestAsync(string methodName, JToken methodParam, Func<JToken, Task<JToken?>> sendRequest)
    {
        if (methodName == NewFileFolders.ApplyEdit)
        {
            if (methodParam is JObject parameters && Prepare(parameters) is string refused)
                return new JObject { ["applied"] = false, ["failureReason"] = refused };
            return await sendRequest(methodParam);
        }
        var result = await sendRequest(methodParam);
        if (result is JObject action && Prepare(action) is string message)
        {
            // Visual Studio applies none of the edit: a group is never removed without its new file.
            var refused = (JObject)action.DeepClone();
            refused["edit"] = new JObject { ["documentChanges"] = new JArray() };
            refused.Remove("command");
            return refused;
        }
        return result;
    }

    /// <summary>Creates the folders the edit needs. Null when the edit may go on, or the refusal, which is reported.</summary>
    private string? Prepare(JObject owner)
    {
        string? refusal;
        try
        {
            var plan = NewFileFolders.Plan(owner.ToString(Formatting.None), solutionDirectory(), new Disk());
            refusal = plan.Outcome switch
            {
                NewFileFolderOutcome.Apply => null,
                NewFileFolderOutcome.Refuse => plan.Reason,
                _ => Create(plan),
            };
        }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            // A plan that fails says nothing about missing folders; Visual Studio applies the edit as before.
            System.Diagnostics.Trace.WriteLine("Runic: new file folders: " + error.Message);
            return null;
        }
        if (refusal != null) _ = RunicSdkState.Background.RunAsync(() => report.ReportAsync(refusal));
        return refusal;
    }

    private static string? Create(NewFileFolderPlan plan)
    {
        foreach (var folder in plan.Folders)
        {
            try { Directory.CreateDirectory(folder); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or NotSupportedException or ArgumentException)
            {
                return NewFileFolders.CreateFailed(plan, folder, error.Message);
            }
        }
        RefusalReport.Note(NewFileFolders.Created(plan));
        return null;
    }

    private sealed class Disk : IFolderState
    {
        public bool DirectoryExists(string path) => Directory.Exists(path);
        public bool FileExists(string path) => File.Exists(path);
    }
}
