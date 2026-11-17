using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Microsoft.VisualStudio;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;
using Microsoft.VisualStudio.TextManager.Interop;
using RunicArtifex.DevKit.VisualStudio.Core;
using RunicArtifex.DevKit.VisualStudio.ToolWindow;

namespace RunicArtifex.DevKit.VisualStudio.DevSession;

/// <summary>
/// <c>dotnet runic dev</c>'s diagnostics in the Error List, as VS Code's <c>$runic-dev</c> problem matcher puts them in
/// the Problems panel (<see cref="DevProblems"/>): <c>dev</c>'s faults on the project file and the build's errors on
/// their source lines, the code first in the description. Help (F1) on a Runic code opens its entry in the diagnostics
/// catalog; activating an entry opens the file at its line. Each project's entries are replaced as a whole, at each new
/// build cycle and when <c>dev</c> starts again.
/// </summary>
internal sealed class DevErrorList
{
    public const string Source = "runic dev";

    private readonly ErrorListProvider provider;
    private readonly Dictionary<string, List<ErrorTask>> tasks = new(StringComparer.OrdinalIgnoreCase);

    public DevErrorList(IServiceProvider services)
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        provider = new ErrorListProvider(services)
        {
            ProviderName = Source,
            ProviderGuid = new Guid("5d7b1a4e-2c9f-4e83-a6b1-8f0d3c7e9a25"),
        };
    }

    /// <summary>Replaces the entries of one project's <c>dev</c>.</summary>
    public void Replace(string project, IVsHierarchy? hierarchy, IReadOnlyList<RunicErrorEntry> entries)
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        provider.SuspendRefresh();
        try
        {
            if (tasks.TryGetValue(project, out var previous))
                foreach (var task in previous) provider.Tasks.Remove(task);
            var added = new List<ErrorTask>();
            foreach (var entry in entries)
            {
                var task = new ErrorTask
                {
                    Category = TaskCategory.BuildCompile,
                    ErrorCategory = entry.Severity switch
                    {
                        RunicErrorSeverity.Error => TaskErrorCategory.Error,
                        RunicErrorSeverity.Warning => TaskErrorCategory.Warning,
                        _ => TaskErrorCategory.Message,
                    },
                    Text = entry.Text,
                    Document = entry.File,
                    Line = entry.Line,
                    Column = entry.Column,
                    HelpKeyword = entry.HelpUri,
                    HierarchyItem = hierarchy,
                };
                task.Navigate += (_, _) => Open(entry);
                task.Help += (_, _) => OpenHelp(entry.HelpUri);
                provider.Tasks.Add(task);
                added.Add(task);
            }
            tasks[project] = added;
        }
        finally { provider.ResumeRefresh(); }
        if (entries.Any(entry => entry.Severity == RunicErrorSeverity.Error)) provider.Show();
    }

    private static void Open(RunicErrorEntry entry)
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        try
        {
            if (!File.Exists(entry.File)) { SdkOutput.WriteLine($"Could not open {entry.File}: the file does not exist."); return; }
            VsShellUtilities.OpenDocument(ServiceProvider.GlobalProvider, entry.File, Guid.Empty, out _, out _, out _, out IVsTextView view);
            if (view == null) return;
            if (ErrorHandler.Failed(view.SetCaretPos(entry.Line, entry.Column))) view.SetCaretPos(entry.Line, 0);
            view.CenterLines(entry.Line, 1);
        }
        catch (Exception error) when (error is not OutOfMemoryException) { SdkOutput.WriteLine("Could not open " + entry.File + ": " + error.Message); }
    }

    private static void OpenHelp(string uri)
    {
        if (Uri.TryCreate(uri, UriKind.Absolute, out var parsed) && parsed.Scheme == Uri.UriSchemeHttps) VsShellUtilities.OpenSystemBrowser(parsed.AbsoluteUri);
    }
}
