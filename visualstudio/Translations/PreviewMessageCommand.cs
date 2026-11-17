using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Interop;
using Microsoft.VisualStudio.ComponentModelHost;
using Microsoft.VisualStudio.Shell;
using RunicArtifex.DevKit.VisualStudio.Commands;
using RunicArtifex.DevKit.VisualStudio.Core;

namespace RunicArtifex.DevKit.VisualStudio.Translations;

/// <summary>
/// Tools → Runic → Preview Message (<c>Tools.RunicPreviewMessage</c>), also in the editor context menu. Shown while
/// an <c>.mf2</c> or <c>.rmf2</c> document is active.
/// </summary>
internal sealed class PreviewMessageCommand : RunicCommandHandler
{
    public override int Id => RunicCommandIds.PreviewMessage;

    public override void BeforeQueryStatus(OleMenuCommand command)
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        command.Visible = command.Enabled = IsMessageDocument(ActiveDocument());
    }

    public override async Task ExecuteAsync(RunicPackage package, CancellationToken cancellationToken)
    {
        await package.JoinableTaskFactory.SwitchToMainThreadAsync(cancellationToken);
        var components = await package.GetServiceAsync(typeof(SComponentModel)) as IComponentModel
            ?? throw new InvalidOperationException("Visual Studio component service is unavailable.");
        var client = components.GetService<RunicLanguageClient>();
        var document = ActiveDocument();
        if (document == null || !IsMessageDocument(document)) throw new InvalidOperationException("Place the cursor inside an MF2 message.");
        var selection = (EnvDTE.TextSelection)document.Selection;
        string uri = new Uri(document.FullName).AbsoluteUri;
        var info = await client.RequestAsync("runic/message", new { textDocument = new { uri }, position = new { line = selection.ActivePoint.Line - 1, character = selection.ActivePoint.LineCharOffset - 1 } }, cancellationToken);
        if (info.Value<bool>("isGroup")) throw new InvalidOperationException("Place the cursor inside a message to preview it.");
        var preview = new PreviewWindow(client, uri, info);
        new WindowInteropHelper(preview).Owner = Process.GetCurrentProcess().MainWindowHandle;
        preview.Show();
        await preview.LoadAsync();
    }

    private static EnvDTE.Document? ActiveDocument()
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        return (Package.GetGlobalService(typeof(EnvDTE.DTE)) as EnvDTE.DTE)?.ActiveDocument;
    }

    private static bool IsMessageDocument(EnvDTE.Document? document)
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        return document != null && (document.FullName.EndsWith(".rmf2", StringComparison.OrdinalIgnoreCase) || document.FullName.EndsWith(".mf2", StringComparison.OrdinalIgnoreCase));
    }
}
