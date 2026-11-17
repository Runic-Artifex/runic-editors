using RunicArtifex.DevKit.VisualStudio.Commands;
using RunicArtifex.DevKit.VisualStudio.Core;

namespace RunicArtifex.DevKit.VisualStudio.DevSession;

/// <summary>Stub. VS-3: starts dotnet runic dev as a child process with an Output pane and Error List entries.</summary>
internal sealed class StartDevCommand : NotAvailableYetHandler
{
    public override int Id => RunicCommandIds.StartDev;
}
