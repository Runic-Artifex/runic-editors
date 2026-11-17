using RunicArtifex.DevKit.VisualStudio.Commands;
using RunicArtifex.DevKit.VisualStudio.Core;

namespace RunicArtifex.DevKit.VisualStudio.Debugging;

/// <summary>Stub. VS-5: starts dev --no-window, builds and debugs the Window through IVsDebugger4 (D070.6: asks first).</summary>
internal sealed class DebugWindowCommand : NotAvailableYetHandler
{
    public override int Id => RunicCommandIds.DebugWindow;
}
