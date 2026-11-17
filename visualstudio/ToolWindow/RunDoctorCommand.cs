using RunicArtifex.DevKit.VisualStudio.Commands;
using RunicArtifex.DevKit.VisualStudio.Core;

namespace RunicArtifex.DevKit.VisualStudio.ToolWindow;

/// <summary>Stub. VS-4a: runs dotnet runic doctor into the Doctor section and the Error List.</summary>
internal sealed class RunDoctorCommand : NotAvailableYetHandler
{
    public override int Id => RunicCommandIds.RunDoctor;
}
