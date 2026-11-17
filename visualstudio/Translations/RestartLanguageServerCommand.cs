using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.VisualStudio.ComponentModelHost;
using RunicArtifex.DevKit.VisualStudio.Commands;
using RunicArtifex.DevKit.VisualStudio.Core;

namespace RunicArtifex.DevKit.VisualStudio.Translations;

/// <summary>Tools → Runic → Restart Language Server (<c>Tools.RunicRestartLanguageServer</c>).</summary>
internal sealed class RestartLanguageServerCommand : RunicCommandHandler
{
    public override int Id => RunicCommandIds.RestartLanguageServer;

    public override async Task ExecuteAsync(RunicPackage package, CancellationToken cancellationToken)
    {
        await package.JoinableTaskFactory.SwitchToMainThreadAsync(cancellationToken);
        var components = await package.GetServiceAsync(typeof(SComponentModel)) as IComponentModel
            ?? throw new InvalidOperationException("Visual Studio component service is unavailable.");
        await components.GetService<RunicLanguageClient>().RestartAsync();
    }
}
