using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;

namespace RunicArtifex.DevKit.VisualStudio.Core;

/// <summary>
/// When Visual Studio loads the Runic DevKit package (PARITY §5): only in a solution with a Runic project. Two UI
/// contexts load it, and the Tools → Runic menu is visible in either:
/// <list type="bullet">
/// <item><see cref="RuleContext"/>, a rule Visual Studio evaluates without loading anything of Runic: a project
/// with the <c>RunicApplication</c> capability (Runic Views, runic-sdk#1080), a project that imports Runic
/// Translations' build props, or an active <c>.mf2</c>/<c>.rmf2</c> editor (the language client's content type).</item>
/// <item><see cref="WorkspaceContext"/>, which a small probe package sets after reading the solution's files with
/// <see cref="RunicWorkspace.Detect"/>: Runic SDK projects of SDKs without the capability, and Translations
/// workspaces without a C# project (a <c>runic.json</c>, or a tool manifest with <c>dotnet-runic-translations</c>).</item>
/// </list>
/// </summary>
public static class RunicActivation
{
    /// <summary>The rule-based UI context; <see cref="RuleExpression"/> over <see cref="RuleTerms"/>.</summary>
    public const string RuleContext = "a3c1e0d8-6f43-4c5e-9b1d-2e7f4a8c9d10";
    public const string RuleName = "Runic DevKit: Runic solution or message editor";
    public const string RuleExpression = SdkProjectTerm + " | " + TranslationsBuildTerm + " | " + MessageEditorTerm;
    public const string SdkProjectTerm = "SdkProject";
    public const string SdkProjectValue = "SolutionHasProjectCapability:" + SdkCapability;
    public const string TranslationsBuildTerm = "TranslationsBuild";
    /// <summary>Runic.Translations.Build.props sets this property in every project that uses the Translations build.</summary>
    public const string TranslationsBuildValue = "SolutionHasProjectBuildProperty:RunicTranslationsToolCommand=.";
    public const string MessageEditorTerm = "MessageEditor";
    public const string MessageEditorValue = "ActiveEditorContentType:" + MessageContentType;

    /// <summary>The custom UI context the probe activates for a solution <see cref="RunicWorkspace.Detect"/> recognizes.</summary>
    public const string WorkspaceContext = "5d9e2b47-1c86-4f0a-a3e5-7b2c6d8f0e41";

    /// <summary>The package that probes each opened solution (loaded with every solution; it only reads files).</summary>
    public const string ProbePackage = "c8f4a2e6-3b71-4d95-8e0c-9a1b5d7f2c63";

    public const string SdkCapability = "RunicApplication";
    public const string MessageContentType = "rmf2";
}

/// <summary>What a solution directory holds, with the rules of VS Code's <c>vscode/src/workspace.ts</c>.</summary>
public sealed class RunicWorkspace
{
    /// <summary>Directories never searched: build output, package installs (and every dot directory, such as <c>.git</c>).</summary>
    private static readonly HashSet<string> Skipped = new(StringComparer.OrdinalIgnoreCase) { "bin", "obj", "node_modules", "artifacts", "dist", "www" };

    /// <summary>How deep below the solution directory projects are searched, as VS Code's <c>searchDepth</c>.</summary>
    public const int SearchDepth = 4;

    private RunicWorkspace(IReadOnlyList<string> sdkProjects, IReadOnlyList<string> translationsProjects, IReadOnlyList<string> toolManifests)
    {
        SdkProjects = sdkProjects; TranslationsProjects = translationsProjects; ToolManifests = toolManifests;
    }

    /// <summary><c>.csproj</c> files of the Runic SDK (<see cref="IsRunicSdkProject"/>), sorted.</summary>
    public IReadOnlyList<string> SdkProjects { get; }
    /// <summary><c>runic.json</c> files, sorted.</summary>
    public IReadOnlyList<string> TranslationsProjects { get; }
    /// <summary><c>.config/dotnet-tools.json</c> files that declare <c>dotnet-runic-translations</c>, sorted.</summary>
    public IReadOnlyList<string> ToolManifests { get; }

    public bool HasSdkProject => SdkProjects.Count > 0;
    public bool IsTranslationsWorkspace => TranslationsProjects.Count > 0 || ToolManifests.Count > 0;
    /// <summary>True when the Runic DevKit package should load for this solution.</summary>
    public bool IsRunic => HasSdkProject || IsTranslationsWorkspace;

    /// <summary>
    /// A project file of the Runic SDK: a Window project (<c>RunicApplicationFrontendWindowProject</c>), or one that
    /// references a <c>Runic.Application.*</c>, <c>Runic.Desktop*</c> or <c>Runic.Platform.*</c> package or project. A
    /// project that references only Runic Translations packages is not one.
    /// </summary>
    public static bool IsRunicSdkProject(string projectText)
        => Regex.IsMatch(projectText, @"<RunicApplicationFrontendWindowProject>\s*true\s*<", RegexOptions.IgnoreCase)
        || Regex.IsMatch(projectText, @"<(?:PackageReference|ProjectReference)\b[^>]*\bInclude\s*=\s*""[^""]*\bRunic\.(?:Application|Desktop|Platform)\b[^""]*""", RegexOptions.IgnoreCase);

    /// <summary>A tool manifest (<c>.config/dotnet-tools.json</c>) that declares the <c>dotnet-runic-translations</c> tool.</summary>
    public static bool IsTranslationsToolManifest(string manifestText)
        => Regex.IsMatch(manifestText, @"""dotnet-runic-translations""\s*:", RegexOptions.IgnoreCase);

    /// <summary>Reads the solution directory and its subdirectories, at most <paramref name="depth"/> levels down.</summary>
    public static RunicWorkspace Detect(string root, int depth = SearchDepth)
    {
        var sdk = new List<string>();
        var translations = new List<string>();
        var manifests = new List<string>();
        void Visit(string directory, int level)
        {
            string manifest = Path.Combine(directory, ".config", "dotnet-tools.json");
            if (File.Exists(manifest) && IsTranslationsToolManifest(Read(manifest))) manifests.Add(manifest);
            IEnumerable<string> files, directories;
            try { files = Directory.EnumerateFiles(directory).ToList(); directories = Directory.EnumerateDirectories(directory).ToList(); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException) { return; }
            foreach (string file in files)
            {
                string name = Path.GetFileName(file);
                if (name.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase)) { if (IsRunicSdkProject(Read(file))) sdk.Add(file); }
                else if (name == "runic.json") translations.Add(file);
            }
            if (level >= depth) return;
            foreach (string child in directories)
            {
                string name = Path.GetFileName(child);
                if (!name.StartsWith(".", StringComparison.Ordinal) && !Skipped.Contains(name)) Visit(child, level + 1);
            }
        }
        if (Directory.Exists(root)) Visit(root, 0);
        return new RunicWorkspace(Sorted(sdk), Sorted(translations), Sorted(manifests));
    }

    private static IReadOnlyList<string> Sorted(List<string> paths) => paths.OrderBy(path => path, StringComparer.Ordinal).ToList();

    private static string Read(string file)
    {
        // An unreadable file is not a Runic one.
        try { return File.ReadAllText(file); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { return ""; }
    }
}
