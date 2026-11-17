using RunicArtifex.DevKit.VisualStudio.Commands;
using RunicArtifex.DevKit.VisualStudio.Core;

namespace RunicArtifex.DevKit.VisualStudio.DevSession;

/// <summary>Stub. VS-3: opens the session file's development server URL.</summary>
internal sealed class OpenDevServerCommand : NotAvailableYetHandler
{
    public override int Id => RunicCommandIds.OpenDevServer;
}
