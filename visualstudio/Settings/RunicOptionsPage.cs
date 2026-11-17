using System;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.VisualStudio;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;
using RunicArtifex.DevKit.VisualStudio.Core;

namespace RunicArtifex.DevKit.VisualStudio.Settings;

/// <summary>
/// Tools → Options → Runic DevKit → General: VS Code's <c>runic.dotnetPath</c>, <c>runic.translations.serverAssembly</c>
/// and <c>runic.translations.trace.server</c>. The deprecated <c>runicTranslations.*</c> settings have no counterpart:
/// Visual Studio never had them. Empty text options use <c>RUNIC_DOTNET</c> and <c>RUNIC_TRANSLATIONS_SERVER</c>.
/// </summary>
[ComVisible(true)]
[Guid("0e7b3d51-8a24-4c6f-9d13-6b5e2f8a7c90")]
public sealed class RunicOptionsPage : DialogPage
{
    [Category("General")]
    [DisplayName(".NET executable")]
    [Description("The dotnet executable that runs the project-local Runic tools (dotnet runic, runic-translations). Empty uses the RUNIC_DOTNET environment variable, else dotnet on the PATH. Applies when the language server next starts.")]
    [DefaultValue("")]
    public string DotnetPath { get; set; } = "";

    [Category("Translations language server")]
    [DisplayName("Language server assembly")]
    [Description("Optional compiled Runic Translations tool DLL (dotnet-runic-translations.dll), absolute or relative to the solution directory, for Translations SDK development. Empty uses the RUNIC_TRANSLATIONS_SERVER environment variable, else dotnet tool run runic-translations. Applies when the language server next starts.")]
    [DefaultValue("")]
    public string ServerAssembly { get; set; } = "";

    [Category("Translations language server")]
    [DisplayName("Trace")]
    [Description("Traces the communication with the Runic language server in the Runic Translations Output pane: Off, Messages (one line per message) or Verbose (with each message's JSON). Applies at once.")]
    [DefaultValue(LspTraceLevel.Off)]
    public LspTraceLevel Trace { get; set; } = LspTraceLevel.Off;

    /// <summary>The options, loading the package first if only the language client (MEF) runs so far.</summary>
    internal static async Task<RunicOptionsPage> GetAsync(CancellationToken cancellationToken)
    {
        await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync(cancellationToken);
        var shell = await AsyncServiceProvider.GlobalProvider.GetServiceAsync(typeof(SVsShell)) as IVsShell
            ?? throw new InvalidOperationException("Visual Studio shell service is unavailable.");
        var guid = new Guid(RunicPackage.PackageGuid);
        if (ErrorHandler.Failed(shell.IsPackageLoaded(ref guid, out IVsPackage loaded)) || loaded == null)
            ErrorHandler.ThrowOnFailure(shell.LoadPackage(ref guid, out loaded));
        return ((RunicPackage)loaded).Options;
    }
}
