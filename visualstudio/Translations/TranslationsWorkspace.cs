using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.VisualStudio;
using Microsoft.VisualStudio.ComponentModelHost;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;
using Microsoft.VisualStudio.TextManager.Interop;
using Microsoft.VisualStudio.Threading;
using RunicArtifex.DevKit.VisualStudio.Core;
using RunicArtifex.DevKit.VisualStudio.Output;
using RunicArtifex.DevKit.VisualStudio.Settings;

namespace RunicArtifex.DevKit.VisualStudio.Translations;

/// <summary>
/// What the Translations commands share (VS Code: <c>extension.ts</c> <c>interchangeFolder</c> and <c>runLocalTool</c>):
/// the runic.json project they act on, running the local <c>runic-translations</c> tool with its output in the
/// "Runic Translations" Output pane, messages and documents.
/// </summary>
internal static class TranslationsWorkspace
{
    /// <summary>The open solution's directory, or null for none.</summary>
    public static async Task<string?> SolutionDirectoryAsync(CancellationToken cancellationToken)
    {
        await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync(cancellationToken);
        if (await AsyncServiceProvider.GlobalProvider.GetServiceAsync(typeof(SVsSolution)) is IVsSolution solution
            && ErrorHandler.Succeeded(solution.GetSolutionInfo(out string directory, out _, out _)) && !string.IsNullOrEmpty(directory))
            return directory;
        return null;
    }

    /// <summary>The file of the active document, or null.</summary>
    public static async Task<string?> ActiveDocumentAsync(CancellationToken cancellationToken)
    {
        await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync(cancellationToken);
        var dte = await AsyncServiceProvider.GlobalProvider.GetServiceAsync(typeof(EnvDTE.DTE)) as EnvDTE.DTE;
        try { return dte?.ActiveDocument?.FullName is { Length: > 0 } file ? file : null; }
        catch (ArgumentException) { return null; }
    }

    /// <summary>The runic.json projects of the solution directory (<see cref="RunicWorkspace.Detect"/>), sorted.</summary>
    public static async Task<IReadOnlyList<string>> ProjectsAsync(CancellationToken cancellationToken)
    {
        string? root = await SolutionDirectoryAsync(cancellationToken);
        await TaskScheduler.Default;
        if (root == null) return Array.Empty<string>();
        var found = new SortedSet<string>(RunicWorkspace.Detect(root).TranslationsProjects, StringComparer.OrdinalIgnoreCase);
        if (TranslationsTool.ProjectManifest(root) is { } conventional) found.Add(conventional);
        return found.ToList();
    }

    /// <summary>
    /// The project to act on: that of the active file (the nearest runic.json above it within the solution directory, as
    /// the language server resolves it); without one, the solution's only project, or the one the user picks. Null when
    /// the user cancels.
    /// </summary>
    public static async Task<string?> ChooseProjectAsync(CancellationToken cancellationToken)
    {
        string? root = await SolutionDirectoryAsync(cancellationToken);
        string? active = await ActiveDocumentAsync(cancellationToken);
        await TaskScheduler.Default;
        if (active != null && TranslationsTool.NearestProjectManifest(active, root) is { } nearest) return nearest;
        var projects = await ProjectsAsync(cancellationToken);
        if (projects.Count == 0) throw new InvalidOperationException("No runic.json project in the solution.");
        if (projects.Count == 1) return projects[0];
        await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync(cancellationToken);
        var picked = ChoiceDialog.Pick("Choose the Runic translation project",
            projects.Select(project => new Choice<string>(TranslationStatus.ProjectLabel(project, root), project, null, project)).ToList());
        return picked?.Value;
    }

    /// <summary>The configured <c>dotnet</c> and server assembly (Tools → Options → Runic DevKit, then the environment variables).</summary>
    public static async Task<TranslationsToolLaunch> LaunchAsync(string project, IReadOnlyList<string> toolArguments, CancellationToken cancellationToken)
    {
        var options = await RunicOptionsPage.GetAsync(cancellationToken);
        string? solutionDirectory = await SolutionDirectoryAsync(cancellationToken);
        string dotnet = RunicSettings.Dotnet(options.DotnetPath, Environment.GetEnvironmentVariable(RunicSettings.DotnetVariable));
        var assembly = RunicSettings.ServerAssembly(options.ServerAssembly, Environment.GetEnvironmentVariable(RunicSettings.ServerAssemblyVariable), solutionDirectory);
        await TaskScheduler.Default;
        return TranslationsTool.Find(Path.GetDirectoryName(project) ?? project, dotnet, assembly, toolArguments);
    }

    /// <summary>
    /// Runs the local tool for <paramref name="project"/>; unless <paramref name="quiet"/>, the command and its output go
    /// to the Runic Translations Output pane. Returns its standard output.
    /// </summary>
    public static async Task<string> RunToolAsync(string project, IReadOnlyList<string> toolArguments, CancellationToken cancellationToken, bool quiet = false)
    {
        var launch = await LaunchAsync(project, toolArguments, cancellationToken);
        var pane = await RunicOutput.TranslationsAsync(cancellationToken);
        await TaskScheduler.Default;
        Action<string>? write = quiet ? null : line => WriteLine(pane, line);
        try { return (await TranslationsTool.RunAsync(launch, toolArguments, write, cancellationToken)).Stdout; }
        catch (InvalidOperationException error) when (quiet)
        {
            WriteLine(pane, "runic-translations " + string.Join(" ", toolArguments.Take(2)) + ": " + error.Message);
            throw;
        }
    }

    /// <summary>A line in an Output pane from any thread (<see cref="IVsOutputWindowPane.OutputStringThreadSafe"/>).</summary>
    public static void WriteLine(IVsOutputWindowPane pane, string line)
    {
#pragma warning disable VSTHRD010 // OutputStringThreadSafe may be called from any thread.
        pane.OutputStringThreadSafe(line + Environment.NewLine);
#pragma warning restore VSTHRD010
    }

    public static async Task ShowOutputAsync(CancellationToken cancellationToken)
        => await RunicOutput.ShowAsync(await RunicOutput.TranslationsAsync(cancellationToken), cancellationToken);

    public static async Task InformAsync(string message, CancellationToken cancellationToken)
    {
        await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync(cancellationToken);
        VsShellUtilities.ShowMessageBox(ServiceProvider.GlobalProvider, message, RunicPackage.Title,
            OLEMSGICON.OLEMSGICON_INFO, OLEMSGBUTTON.OLEMSGBUTTON_OK, OLEMSGDEFBUTTON.OLEMSGDEFBUTTON_FIRST);
    }

    /// <summary>OK/Cancel question; true for OK.</summary>
    public static async Task<bool> ConfirmAsync(string message, CancellationToken cancellationToken)
    {
        await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync(cancellationToken);
        return VsShellUtilities.ShowMessageBox(ServiceProvider.GlobalProvider, message, RunicPackage.Title,
            OLEMSGICON.OLEMSGICON_QUERY, OLEMSGBUTTON.OLEMSGBUTTON_OKCANCEL, OLEMSGDEFBUTTON.OLEMSGDEFBUTTON_FIRST) == (int)VSConstants.MessageBoxResult.IDOK;
    }

    /// <summary>Opens a file with the caret on a 0-based line.</summary>
    public static async Task OpenAsync(string file, int line, CancellationToken cancellationToken)
    {
        await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync(cancellationToken);
        VsShellUtilities.OpenDocument(ServiceProvider.GlobalProvider, file, Guid.Empty, out _, out _, out _, out IVsTextView view);
        if (view == null) return;
        view.SetCaretPos(line, 0);
        view.CenterLines(line, 1);
    }

    /// <summary>
    /// Writes generated text (the status report, a library source) to a read-only file under the extension's temporary
    /// directory, replacing an earlier copy, so Visual Studio opens it read-only.
    /// </summary>
    public static void WriteReadOnly(string path, string text)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        if (File.Exists(path)) File.SetAttributes(path, FileAttributes.Normal);
        File.WriteAllText(path, text, new System.Text.UTF8Encoding(false));
        File.SetAttributes(path, FileAttributes.ReadOnly);
    }

    /// <summary>The extension's temporary directory for generated read-only documents.</summary>
    public static string TemporaryDirectory(string kind) => Path.Combine(Path.GetTempPath(), "RunicDevKit", kind);

    /// <summary>The open documents with their unsaved state.</summary>
    public static IReadOnlyList<(OpenDocument Document, EnvDTE.Document Source)> OpenDocuments()
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        var documents = new List<(OpenDocument, EnvDTE.Document)>();
        if (ServiceProvider.GlobalProvider.GetService(typeof(EnvDTE.DTE)) is not EnvDTE.DTE dte || dte.Documents == null) return documents;
        foreach (EnvDTE.Document document in dte.Documents)
        {
            try { documents.Add((new OpenDocument(document.FullName, !document.Saved), document)); }
            catch (ArgumentException) { }
        }
        return documents;
    }

    /// <summary>The Runic language client (MEF), for custom requests.</summary>
    public static async Task<RunicLanguageClient> LanguageClientAsync(CancellationToken cancellationToken)
    {
        await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync(cancellationToken);
        var components = await AsyncServiceProvider.GlobalProvider.GetServiceAsync(typeof(SComponentModel)) as IComponentModel
            ?? throw new InvalidOperationException("Visual Studio component service is unavailable.");
        return components.GetService<RunicLanguageClient>();
    }
}
