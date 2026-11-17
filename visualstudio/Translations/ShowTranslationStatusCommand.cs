using System.IO;
using System.Threading;
using System.Threading.Tasks;
using RunicArtifex.DevKit.VisualStudio.Commands;
using RunicArtifex.DevKit.VisualStudio.Core;

namespace RunicArtifex.DevKit.VisualStudio.Translations;

/// <summary>
/// Tools → Runic → Show Translation Status (also the Translations section's toolbar): <c>runic-translations status</c> of
/// the project, the same report as the command line and the Translations Editor, as a read-only Markdown document (VS
/// Code's Translation Status panel). The Translations section shows the same numbers.
/// </summary>
internal sealed class ShowTranslationStatusCommand : RunicCommandHandler
{
    public override int Id => RunicCommandIds.ShowTranslationStatus;

    public override async Task ExecuteAsync(RunicPackage package, CancellationToken cancellationToken)
    {
        string? project = await TranslationsWorkspace.ChooseProjectAsync(cancellationToken);
        if (project != null) await ShowAsync(project, cancellationToken);
    }

    /// <summary>Reads the status of <paramref name="project"/> and opens its report.</summary>
    public static async Task ShowAsync(string project, CancellationToken cancellationToken)
    {
        var result = TranslationStatus.Parse(await TranslationsWorkspace.RunToolAsync(project, TranslationsTool.StatusArguments(project), cancellationToken));
        TranslationStatusState.Instance.Store(project, result);
        string catalog = result.Projects.Count > 0 ? result.Projects[0].Catalog : "";
        string name = string.Concat((catalog.Length > 0 ? catalog : "project").Split(Path.GetInvalidFileNameChars()));
        // One document per project, replaced on each run; Visual Studio reloads it when it is open.
        string path = Path.Combine(TranslationsWorkspace.TemporaryDirectory("status"), StableFolder(project), $"Translation Status · {name}.md");
        TranslationsWorkspace.WriteReadOnly(path, TranslationStatus.Markdown(result));
        await TranslationsWorkspace.OpenAsync(path, 0, cancellationToken);
    }

    private static string StableFolder(string project)
    {
        using var sha = System.Security.Cryptography.SHA256.Create();
        byte[] hash = sha.ComputeHash(System.Text.Encoding.UTF8.GetBytes(project.ToLowerInvariant()));
        return System.BitConverter.ToString(hash, 0, 6).Replace("-", "").ToLowerInvariant();
    }
}
