using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using TUnit.Core;

namespace RunicArtifex.DevKit.VisualStudio.Core.Tests;

/// <summary>DEV-SEC-86: Visual Studio starts the language server outside the open solution only after the user trusts the folder.</summary>
public class ServerTrustTests
{
    private static readonly string Root = Path.Combine(Path.GetTempPath(), "runic-trust");
    private static readonly string Solution = Path.Combine(Root, "repo", "src");

    private static ServerTrustDecision Decide(string manifest, string? solution = null, IEnumerable<string>? trusted = null, ICollection<string>? declined = null)
    {
        var known = new HashSet<string>(trusted ?? Array.Empty<string>(), StringComparer.OrdinalIgnoreCase);
        return ServerTrust.Decide(manifest, solution, known.Contains, declined ?? new List<string>());
    }

    [Test]
    public void A_manifest_in_the_open_solutions_folder_starts_at_once()
    {
        Check.Equal(ServerTrustDecision.Start, Decide(Solution, Solution));
        Check.Equal(ServerTrustDecision.Start, Decide(Path.Combine(Solution, "Translations"), Solution));
        Check.Equal(ServerTrustDecision.Start, Decide(Path.Combine(Solution, "Translations") + Path.DirectorySeparatorChar, Solution + Path.DirectorySeparatorChar));
    }

    [Test]
    public void Other_folders_ask_until_trusted_and_a_no_holds()
    {
        string above = Path.Combine(Root, "repo");
        Check.Equal(ServerTrustDecision.Ask, Decide(above, Solution), "a manifest above the solution");
        Check.Equal(ServerTrustDecision.Ask, Decide(Path.Combine(Root, "repo", "src-other"), Solution), "a sibling with the same prefix");
        Check.Equal(ServerTrustDecision.Ask, Decide(Path.Combine(Root, "Downloads"), solution: null), "a lone document");
        Check.Equal(ServerTrustDecision.Start, Decide(above, Solution, trusted: new[] { ServerTrust.Key(above) }));
        Check.Equal(ServerTrustDecision.Declined, Decide(above, Solution, declined: new List<string> { ServerTrust.Key(above + Path.DirectorySeparatorChar) }));
        Check.Equal(ServerTrustDecision.Ask, Decide(Path.GetPathRoot(Root)!, Solution), "a manifest in the drive root");
    }

    [Test]
    public void Trusted_folders_are_stored_one_per_line()
    {
        string stored = ServerTrust.AddFolder(null, "C:\\a");
        stored = ServerTrust.AddFolder(stored, "C:\\b");
        stored = ServerTrust.AddFolder(stored, "c:\\A");
        Check.Same(new[] { "C:\\a", "C:\\b" }, ServerTrust.ParseFolders(stored));
        Check.Equal(0, ServerTrust.ParseFolders("").Count);
    }

    [Test]
    public void The_question_and_the_refusal_name_the_folder_and_the_way_back()
    {
        string question = ServerTrust.Question("C:\\repo", "C:\\repo\\src");
        Check.True(question.Contains("C:\\repo?") && question.Contains("outside the open solution's folder (C:\\repo\\src)") && question.Contains("global.json"), question);
        string folder = ServerTrust.Question("D:\\Downloads", null);
        Check.True(folder.Contains("a folder or a document open without a solution file") && !folder.Contains("No solution is open"), folder);
        string project = ServerTrust.Question("C:\\Published", null, Path.Combine("C:", "Published", "Published.csproj"));
        Check.True(project.Contains("opened the project Published.csproj without a solution file"), "a project opened without a .sln (0.24.0 checklist P5): " + project);
        var attempt = new ServerStartAttempt { Known = ServerStartProblem.NotTrusted, Error = ServerTrust.DeclinedMessage("C:\\repo") };
        string message = ServerStartFailure.Message(attempt);
        Check.True(message.Contains("did not trust that folder") && message.Contains("Restart Language Server"), message);
    }
}
