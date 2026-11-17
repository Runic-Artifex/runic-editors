namespace RunicArtifex.DevKit.VisualStudio.Core;

/// <summary>
/// A line-oriented output target, such as the "Runic SDK" Output pane (VS Code's "Runic" channel). Features that log
/// take one so that the Output pane's owner can provide it; until then they write to the diagnostic trace.
/// </summary>
public interface IRunicOutput
{
    void WriteLine(string line);
}
