using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace RunicArtifex.DevKit.VisualStudio.Core;

/// <summary>Why the language server did not start.</summary>
public enum ServerStartProblem
{
    /// <summary>No tool manifest declares <c>runic-translations</c> above the solution or document.</summary>
    ToolMissing,
    /// <summary>The manifest declares the tool, but <c>dotnet tool restore</c> has not installed it.</summary>
    RestoreNeeded,
    /// <summary>The .NET runtime the tool needs is not installed.</summary>
    RuntimeMissing,
    /// <summary>The restored tool is too old for the language server (no <c>lsp</c> command).</summary>
    WrongToolVersion,
    /// <summary>The configured dotnet executable does not exist.</summary>
    DotnetMissing,
    /// <summary>The configured server assembly is not an existing absolute DLL.</summary>
    AssemblyMissing,
    /// <summary>The user did not trust the folder the server would start in (DEV-SEC-86).</summary>
    NotTrusted,
    /// <summary>Anything else; the Output pane has the details.</summary>
    Other,
}

/// <summary>What the extension knows about a failed start.</summary>
public sealed class ServerStartAttempt
{
    /// <summary>The error message, if the start threw or Visual Studio reported one.</summary>
    public string? Error { get; set; }
    /// <summary>The problem when the extension found it itself (before starting a process).</summary>
    public ServerStartProblem? Known { get; set; }
    /// <summary>Starting the dotnet executable failed because it was not found.</summary>
    public bool ExecutableMissing { get; set; }
    public string Dotnet { get; set; } = RunicSettings.DefaultDotnet;
    /// <summary>The tool-manifest directory the server ran in, if one was found.</summary>
    public string? Directory { get; set; }
    /// <summary>The server's last standard-error lines.</summary>
    public IReadOnlyList<string> Stderr { get; set; } = Array.Empty<string>();
}

/// <summary>
/// The start-failure InfoBar (PARITY §5): VS Code's notification (<c>extension.ts</c> <c>reportStartFailure</c>), once
/// per solution, with "Show output" and "How to install the tool", and a reason that says what to do.
/// </summary>
public static class ServerStartFailure
{
    public const string ShowOutput = "Show output";
    public const string HowToInstall = "How to install the tool";
    /// <summary>The tool's installation steps, the same for every editor.</summary>
    public const string InstallHelpUrl = "https://github.com/Runic-Artifex/runic-editors/blob/main/vscode/README.md#install-the-tool";

    public static ServerStartProblem Classify(ServerStartAttempt attempt)
    {
        if (attempt.Known is { } known) return known;
        if (attempt.ExecutableMissing) return ServerStartProblem.DotnetMissing;
        string text = string.Join("\n", attempt.Stderr.Concat(new[] { attempt.Error ?? "" }));
        if (Regex.IsMatch(text, @"dotnet tool restore|is not installed|Cannot find a tool in the manifest file", RegexOptions.IgnoreCase))
            return ServerStartProblem.RestoreNeeded;
        if (Regex.IsMatch(text, @"install or update \.NET|framework 'Microsoft\.NETCore\.App'|No frameworks were found", RegexOptions.IgnoreCase))
            return ServerStartProblem.RuntimeMissing;
        if (Regex.IsMatch(text, @"unknown command 'lsp'|Unrecognized command or argument 'lsp'", RegexOptions.IgnoreCase))
            return ServerStartProblem.WrongToolVersion;
        if (Regex.IsMatch(text, @"No project-local runic-translations tool", RegexOptions.IgnoreCase)) return ServerStartProblem.ToolMissing;
        return ServerStartProblem.Other;
    }

    /// <summary>The InfoBar text: what failed, and the next step.</summary>
    public static string Message(ServerStartAttempt attempt)
    {
        const string prefix = "Runic Translations could not start its language server. ";
        string where = string.IsNullOrEmpty(attempt.Directory) ? "the solution directory" : attempt.Directory!;
        return prefix + Classify(attempt) switch
        {
            ServerStartProblem.ToolMissing => "No .config/dotnet-tools.json declares the runic-translations tool. Install dotnet-runic-translations as a local tool, then restart the language server.",
            ServerStartProblem.RestoreNeeded => $"The local runic-translations tool is not restored. Run dotnet tool restore in {where}, then restart the language server.",
            ServerStartProblem.RuntimeMissing => "The runic-translations tool needs a .NET runtime that is not installed; the Output pane names it. Install it, then restart the language server.",
            ServerStartProblem.WrongToolVersion => "The restored runic-translations tool has no language server (lsp). Use the version of the project's Runic Translations packages in .config/dotnet-tools.json, run dotnet tool restore, then restart the language server.",
            ServerStartProblem.DotnetMissing => $"The dotnet executable '{attempt.Dotnet}' was not found. Install .NET, or set .NET executable in Tools > Options > {RunicSettings.OptionsCategory}.",
            ServerStartProblem.AssemblyMissing => Summary(attempt.Error) + " Fix the setting, then restart the language server.",
            ServerStartProblem.NotTrusted => Summary(attempt.Error),
            _ => Summary(attempt.Error ?? attempt.Stderr.LastOrDefault()),
        };
    }

    /// <summary>First line of an error, as VS Code's <c>errorSummary</c>.</summary>
    public static string Summary(string? error)
    {
        string line = (error ?? "").Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries).FirstOrDefault()?.Trim() ?? "";
        return line.Length == 0 ? "See the Runic Translations Output pane." : line;
    }
}

/// <summary>Remembers which keys (solutions) were already told, as VS Code's <c>OneTimeNotice</c>.</summary>
public sealed class OneTimeNotice
{
    private readonly HashSet<string> claimed = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>True the first time for <paramref name="key"/>.</summary>
    public bool Claim(string key) { lock (claimed) return claimed.Add(key); }
}

/// <summary>The two Output window panes (VS Code's two output channels).</summary>
public static class RunicOutputPanes
{
    public const string SdkName = "Runic SDK";
    public const string SdkGuid = "6b0f7d24-91a3-4e8c-b5d2-3f7a9c1e4d58";
    public const string TranslationsName = "Runic Translations";
    public const string TranslationsGuid = "e2a84c19-5d7b-4f36-8a01-c9b3e6d2f475";
}
