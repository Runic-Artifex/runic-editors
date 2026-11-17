using RunicArtifex.DevKit.VisualStudio.Commands;
using RunicArtifex.DevKit.VisualStudio.Core;

namespace RunicArtifex.DevKit.VisualStudio.DevSession;

/// <summary>Stub. VS-3: starts dotnet runic dev --no-window.</summary>
internal sealed class StartDevNoWindowCommand : NotAvailableYetHandler
{
    public override int Id => RunicCommandIds.StartDevNoWindow;
}
