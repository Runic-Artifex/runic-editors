using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;
using RunicArtifex.DevKit.VisualStudio.Core;

namespace RunicArtifex.DevKit.VisualStudio.ToolWindow;

/// <summary>
/// Doctor findings in the Error List, as VS Code's Problems on the project file (<c>sdk.ts</c>
/// <c>publishDoctorDiagnostics</c>): one entry per failing or warning check, the check ID first in the description,
/// the remediation after it. Help (F1) on an entry opens the doctor checks' documentation at the project's tool version;
/// activating it opens the project file. The Error List's context menu has Run dotnet runic doctor (Commands.vsct).
/// </summary>
internal sealed class DoctorErrorList
{
    private readonly ErrorListProvider provider;

    public DoctorErrorList(IServiceProvider services)
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        provider = new ErrorListProvider(services)
        {
            ProviderName = Doctor.Source,
            ProviderGuid = new Guid("0b6a2f8e-4f3c-4f61-9b0e-52c6a7d6c1f4"),
        };
    }

    /// <summary>Replaces the project's doctor entries.</summary>
    public void Replace(string project, IVsHierarchy? hierarchy, IReadOnlyList<RunicErrorEntry> entries)
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        provider.SuspendRefresh();
        try
        {
            foreach (var task in provider.Tasks.OfType<ErrorTask>().Where(task => string.Equals(task.Document, project, StringComparison.OrdinalIgnoreCase)).ToList())
                provider.Tasks.Remove(task);
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
                task.Navigate += (_, _) => Open(entry.File);
                task.Help += (_, _) => OpenHelp(entry.HelpUri);
                provider.Tasks.Add(task);
            }
        }
        finally { provider.ResumeRefresh(); }
    }

    public void Show()
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        provider.Show();
    }

    private static void Open(string file)
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        try { VsShellUtilities.OpenDocument(ServiceProvider.GlobalProvider, file); }
        catch (Exception error) when (error is not OutOfMemoryException) { SdkOutput.WriteLine("Could not open " + file + ": " + error.Message); }
    }

    private static void OpenHelp(string uri)
    {
        if (Uri.TryCreate(uri, UriKind.Absolute, out var parsed) && parsed.Scheme == Uri.UriSchemeHttps) VsShellUtilities.OpenSystemBrowser(parsed.AbsoluteUri);
    }
}
