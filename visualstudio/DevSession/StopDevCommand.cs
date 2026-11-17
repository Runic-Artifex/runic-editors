using RunicArtifex.DevKit.VisualStudio.Commands;
using RunicArtifex.DevKit.VisualStudio.Core;

namespace RunicArtifex.DevKit.VisualStudio.DevSession;

/// <summary>Stub. VS-3: stops the dotnet runic dev this extension started.</summary>
internal sealed class StopDevCommand : NotAvailableYetHandler
{
    public override int Id => RunicCommandIds.StopDev;
}
