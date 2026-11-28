using TUnit.Core;

namespace RunicArtifex.DevKit.VisualStudio.Core.Tests;

public class MigrationTests
{
    [Test]
    public void Open_Manage_Extensions_tries_the_Visual_Studio_18_command_first_then_the_former_name()
    {
        // Visual Studio 18.12 rejects Extensions.ManageExtensions as invalid (0.24.0 checklist P3).
        Check.Same(new[] { "ManageExtensions", "Extensions.ManageExtensions" }, ManageExtensionsCommand.Candidates);
        Check.True(ManageExtensionsCommand.Fallback.Contains("Extensions > Manage Extensions"), ManageExtensionsCommand.Fallback);
    }
}
