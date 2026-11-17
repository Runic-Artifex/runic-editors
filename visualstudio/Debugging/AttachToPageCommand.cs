using RunicArtifex.DevKit.VisualStudio.Commands;
using RunicArtifex.DevKit.VisualStudio.Core;

namespace RunicArtifex.DevKit.VisualStudio.Debugging;

/// <summary>Stub. VS-5: attaches the JavaScript debugger to the page over CDP.</summary>
internal sealed class AttachToPageCommand : NotAvailableYetHandler
{
    public override int Id => RunicCommandIds.AttachToPage;
}
