using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace RunicArtifex.DevKit.VisualStudio.Core;

/// <summary>What the language server's trust gate decides before a start (DEV-SEC-86).</summary>
public enum ServerTrustDecision
{
    /// <summary>Start: the folder is the open solution's (or inside it), or the user trusted it before.</summary>
    Start,
    /// <summary>Ask the user first.</summary>
    Ask,
    /// <summary>Do not start: the user declined in this Visual Studio session.</summary>
    Declined,
}

/// <summary>
/// The language server's trust gate (DEV-SEC-86), Visual Studio's counterpart of VS Code's workspace trust. Starting the
/// server runs <c>dotnet tool run runic-translations</c> in the tool manifest's folder, and <c>dotnet</c> reads that
/// folder's <c>global.json</c>, NuGet configuration and tool manifest, so a folder can make it run code of its own.
/// Visual Studio documents no extension API for its trust decision (18.12 only references an internal, undocumented
/// <c>SVsTrustService</c>); a solution it opened has passed its own checks (Mark of
/// the Web) and already runs its projects' code in design-time builds, so a manifest in the solution's folder starts at
/// once. Any other folder (a lone document, a manifest above the solution, a folder another user can write such as
/// <c>C:\</c>) needs the user's yes once, remembered per folder.
/// </summary>
public static class ServerTrust
{
    /// <summary>Visual Studio's user settings collection and property that hold the trusted folders, one per line.</summary>
    public const string Collection = "RunicArtifex.DevKit";
    public const string Property = "TrustedLanguageServerFolders";

    /// <summary>The stored folders.</summary>
    public static IReadOnlyList<string> ParseFolders(string? stored)
        => (stored ?? "").Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries).Select(line => line.Trim()).Where(line => line.Length > 0).ToList();

    /// <summary>The stored value with <paramref name="folder"/> added once.</summary>
    public static string AddFolder(string? stored, string folder)
    {
        var folders = ParseFolders(stored).ToList();
        if (!folders.Contains(folder, StringComparer.OrdinalIgnoreCase)) folders.Add(folder);
        return string.Join("\n", folders);
    }

    public static ServerTrustDecision Decide(string manifestFolder, string? solutionFolder, Func<string, bool> trusted, ICollection<string> declined)
    {
        if (solutionFolder != null && IsWithin(manifestFolder, solutionFolder)) return ServerTrustDecision.Start;
        string key = Key(manifestFolder);
        if (trusted(key)) return ServerTrustDecision.Start;
        return declined.Contains(key) ? ServerTrustDecision.Declined : ServerTrustDecision.Ask;
    }

    /// <summary>The folder's full path without a trailing separator, the key the decision is remembered under.</summary>
    public static string Key(string folder)
    {
        string full = Path.GetFullPath(folder);
        string trimmed = full.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        // A drive or file-system root keeps its separator (C:\, /).
        return trimmed.Length == 0 || trimmed.EndsWith(":", StringComparison.Ordinal) ? full : trimmed;
    }

    /// <summary>Whether <paramref name="path"/> is <paramref name="root"/> or inside it (by path, case-insensitively as on Windows).</summary>
    public static bool IsWithin(string path, string root)
    {
        string child = Key(path), parent = Key(root);
        if (string.Equals(child, parent, StringComparison.OrdinalIgnoreCase)) return true;
        string prefix = parent.EndsWith(Path.DirectorySeparatorChar.ToString(), StringComparison.Ordinal) ? parent : parent + Path.DirectorySeparatorChar;
        return child.StartsWith(prefix, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>The question before the first start in a folder outside the open solution.</summary>
    public static string Question(string manifestFolder, string? solutionFolder) =>
        $"Start the Runic Translations language server in {manifestFolder}?{Environment.NewLine}{Environment.NewLine}"
        + (solutionFolder == null ? "No solution is open, so Visual Studio has not opened this folder as a project. " : $"The folder is outside the open solution's folder ({solutionFolder}). ")
        + "Runic DevKit starts the server with dotnet tool run runic-translations, and dotnet uses that folder's tool manifest, global.json and NuGet configuration, which can run code from the folder. "
        + "Start it only if you trust the folder. Runic DevKit remembers a Yes for this folder; after a No, Tools > Runic > Restart Language Server asks again.";

    /// <summary>The start failure's text after a No.</summary>
    public static string DeclinedMessage(string manifestFolder) =>
        $"The language server was not started in {manifestFolder}, because you did not trust that folder. Use Tools > Runic > Restart Language Server to be asked again.";
}
