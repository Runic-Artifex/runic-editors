using RunicArtifex.DevKit.VisualStudio.Commands;
using RunicArtifex.DevKit.VisualStudio.Core;

namespace RunicArtifex.DevKit.VisualStudio.Settings;

/// <summary>Stub. VS-2: Tools > Options > Runic (.NET executable, server assembly, trace, F5 behaviour).</summary>
internal sealed class OpenOptionsCommand : NotAvailableYetHandler
{
    public override int Id => RunicCommandIds.OpenOptions;
}
