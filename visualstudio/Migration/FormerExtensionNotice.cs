using System;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.VisualStudio;
using Microsoft.VisualStudio.Imaging;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;
using RunicArtifex.DevKit.VisualStudio.Core;

namespace RunicArtifex.DevKit.VisualStudio.Migration;

/// <summary>
/// Runic MF2 Translations (<c>Runic.Artifex.Translations.Rmf2</c>, up to 0.21) is this extension's former identity
/// (D070.4, D070.5). Visual Studio installs Runic DevKit next to it, and both would start a language server for the
/// same files, so an InfoBar says how to uninstall it, once per session.
/// </summary>
internal static class FormerExtensionNotice
{
    internal const string FormerId = "Runic.Artifex.Translations.Rmf2";
    internal const string FormerName = "Runic MF2 Translations";
    internal const string Text = "Runic DevKit replaces " + FormerName + ", which is still installed. Both start a Runic language server for the same files. "
        + "Uninstall it: Extensions > Manage Extensions > Installed, select " + FormerName + ", choose Uninstall, then restart Visual Studio.";
    private const string ManageExtensions = "Open Manage Extensions";

    public static async Task ShowIfInstalledAsync(AsyncPackage package, CancellationToken cancellationToken)
    {
        try
        {
            if (!await IsInstalledAsync(package, FormerId)) return;
            await package.JoinableTaskFactory.SwitchToMainThreadAsync(cancellationToken);
            if (await package.GetServiceAsync(typeof(SVsShell)) is not IVsShell shell
                || await package.GetServiceAsync(typeof(SVsInfoBarUIFactory)) is not IVsInfoBarUIFactory factory) return;
            if (ErrorHandler.Failed(shell.GetProperty((int)__VSSPROPID7.VSSPROPID_MainWindowInfoBarHost, out object value)) || value is not IVsInfoBarHost host) return;
            var model = new InfoBarModel(new[] { new InfoBarTextSpan(Text) },
                new IVsInfoBarActionItem[] { new InfoBarHyperlink(ManageExtensions) }, KnownMonikers.StatusWarning, isCloseButtonVisible: true);
            var element = factory.CreateInfoBar(model);
            element.Advise(new Events(), out _);
            host.AddInfoBar(element);
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            // The notice is advice; never fail the package over it.
            ActivityLog.TryLogWarning("Runic DevKit", "Could not check for " + FormerName + ": " + error.Message);
        }
    }

    /// <summary>
    /// <c>IVsExtensionManager.TryGetInstalledExtension</c>. No reference package ships the extension manager's
    /// assembly, so it is reached through the copy Visual Studio has loaded.
    /// </summary>
    private static async Task<bool> IsInstalledAsync(AsyncPackage package, string id)
    {
        const string ns = "Microsoft.VisualStudio.ExtensionManager.";
        var assemblies = AppDomain.CurrentDomain.GetAssemblies().ToList();
        if (!assemblies.Any(assembly => assembly.GetName().Name == "Microsoft.VisualStudio.ExtensionManager"))
            assemblies.Add(Assembly.Load("Microsoft.VisualStudio.ExtensionManager"));
        Type? Find(string name) => assemblies.Select(assembly => assembly.GetType(ns + name, throwOnError: false)).FirstOrDefault(type => type != null);
        Type service = Find("SVsExtensionManager") ?? throw new InvalidOperationException("SVsExtensionManager is unavailable.");
        Type manager = Find("IVsExtensionManager") ?? throw new InvalidOperationException("IVsExtensionManager is unavailable.");
        object instance = await package.GetServiceAsync(service) ?? throw new InvalidOperationException("The extension manager service is unavailable.");
        var method = manager.GetMethod("TryGetInstalledExtension") ?? throw new InvalidOperationException("TryGetInstalledExtension is unavailable.");
        object?[] arguments = { id, null };
        return method.Invoke(instance, arguments) is true && arguments[1] != null;
    }

    private sealed class Events : IVsInfoBarUIEvents
    {
        public void OnClosed(IVsInfoBarUIElement infoBarUIElement) { }

        public void OnActionItemClicked(IVsInfoBarUIElement infoBarUIElement, IVsInfoBarActionItem actionItem)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            if (Package.GetGlobalService(typeof(EnvDTE.DTE)) is EnvDTE.DTE dte)
                foreach (string command in ManageExtensionsCommand.Candidates)
                {
                    try { dte.ExecuteCommand(command); return; }
                    catch (Exception error) { ActivityLog.TryLogWarning("Runic DevKit", $"Could not open Manage Extensions with {command}: {error.Message}"); }
                }
            VsClosing.ShowMessage(ServiceProvider.GlobalProvider, ManageExtensionsCommand.Fallback, OLEMSGICON.OLEMSGICON_INFO);
        }
    }
}
