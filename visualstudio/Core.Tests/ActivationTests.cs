using System;
using System.IO;
using System.Linq;
using System.Xml.Linq;
using TUnit.Core;

namespace RunicArtifex.DevKit.VisualStudio.Core.Tests;

/// <summary>When the package loads: the rule context, the probe's file rules (VS Code's workspace.ts) and their wiring.</summary>
public class ActivationTests
{
    private const string TranslationsManifest = """{ "version": 1, "isRoot": true, "tools": { "dotnet-runic-translations": { "version": "0.13.0", "commands": [ "runic-translations" ] } } }""";

    [Test]
    public void Sdk_projects_are_window_projects_or_reference_the_runic_sdk()
    {
        Check.True(RunicWorkspace.IsRunicSdkProject("<Project><PropertyGroup><RunicApplicationFrontendWindowProject> true </RunicApplicationFrontendWindowProject></PropertyGroup></Project>"), "window project");
        Check.True(RunicWorkspace.IsRunicSdkProject("""<PackageReference Include="Runic.Application.Views" Version="0.21.0" />"""), "Runic.Application.*");
        Check.True(RunicWorkspace.IsRunicSdkProject("""<packagereference Version="1" include="Runic.Desktop.WebView2" />"""), "Runic.Desktop*, any case and order");
        Check.True(RunicWorkspace.IsRunicSdkProject("""<ProjectReference Include="..\..\src\Runic.Platform.Linux\Runic.Platform.Linux.csproj" />"""), "Runic.Platform.* project");
        Check.True(!RunicWorkspace.IsRunicSdkProject("""<PackageReference Include="Runic.Translations" Version="0.13.0" />"""), "Translations only");
        Check.True(!RunicWorkspace.IsRunicSdkProject("<RunicApplicationFrontendWindowProject>false</RunicApplicationFrontendWindowProject>"), "explicitly not a window");
        Check.True(!RunicWorkspace.IsRunicSdkProject("""<PackageReference Include="NotRunic.ApplicationX" />"""), "lookalike");
    }

    [Test]
    public void A_translations_tool_manifest_declares_dotnet_runic_translations()
    {
        Check.True(RunicWorkspace.IsTranslationsToolManifest(TranslationsManifest), "the repository's manifest");
        Check.True(RunicWorkspace.IsTranslationsToolManifest("""{"tools":{"Dotnet-Runic-Translations" :{}}}"""), "NuGet IDs are case-insensitive");
        Check.True(!RunicWorkspace.IsTranslationsToolManifest("""{ "tools": { "dotnet-runic": { "version": "0.21.0" } } }"""), "dotnet-runic only");
        Check.True(!RunicWorkspace.IsTranslationsToolManifest("""{ "tools": { "x": { "description": "not dotnet-runic-translations" } } }"""), "a value, not a tool");
        Check.True(RunicWorkspace.IsTranslationsToolManifest("""{ "tools": { "runicartifex.translations.tool": { "version": "0.17.0", "commands": [ "rax-translations" ] } } }"""), "RunicArtifex.Translations.Tool");
        Check.True(!RunicWorkspace.IsTranslationsToolManifest("""{ "tools": { "runicartifex.tool": {} } }"""), "the core tool only");
    }

    [Test]
    public void The_core_tool_is_RunicArtifex_Tool_or_dotnet_runic_in_either_manifest_location()
    {
        Check.Equal("rax", RunicTools.Find("""{ "tools": { "runicartifex.tool": { "version": "0.25.0", "commands": [ "rax" ] } } }""")?.Tool.Verb);
        Check.Equal("runic", RunicTools.Find("""{ "tools": { "dotnet-runic": { "version": "0.24.0" } } }""")?.Tool.Verb);
        Check.Equal("0.25.0", RunicTools.Find("""{ "tools": { "dotnet-runic": { "version": "0.24.0" }, "RunicArtifex.Tool": { "version": "0.25.0" } } }""")?.Version, "the new ID wins mid-migration");
        Check.Equal(null, RunicTools.Find("""{ "tools": { "dotnet-ef": {} } }"""));
        Check.Equal(null, RunicTools.Find("not json"));
        Check.Equal("rax-translations", RunicTools.TranslationsCommand("""{ "tools": { "a": { "commands": [ "runic-translations" ] }, "b": { "commands": [ "rax-translations" ] } } }"""));
        Check.Equal("runic-translations", RunicTools.TranslationsCommand(TranslationsManifest));

        using var root = new TempDirectory();
        root.Write("src/App/App.csproj", "<Project />");
        string project = root.Full("src/App/App.csproj");
        root.Write("dotnet-tools.json", """{ "isRoot": true, "tools": { "runicartifex.tool": { "version": "0.25.0" } } }""");
        Check.Equal("0.25.0", RunicSdkProjects.ProjectToolVersion(project), "the root dotnet-tools.json of .NET 10");
        Check.Equal(root.Full("dotnet-tools.json"), RunicTools.ProjectCoreTool(project)?.Manifest);
        root.Write(".config/dotnet-tools.json", """{ "tools": { "dotnet-runic": { "version": "0.24.0" } } }""");
        Check.Equal("0.24.0", RunicSdkProjects.ProjectToolVersion(project), ".config/dotnet-tools.json is read first");
        root.Write("src/dotnet-tools.json", """{ "isRoot": true, "tools": {} }""");
        Check.Equal(null, RunicSdkProjects.ProjectToolVersion(project), "a nearer root manifest ends the search");
        Check.Equal(root.Full("src"), RunicTools.ManifestDirectory(root.Full("src/dotnet-tools.json")));
        Check.Equal(root.Path, RunicTools.ManifestDirectory(root.Full(".config/dotnet-tools.json")));
    }

    [Test]
    public void Former_tool_IDs_get_a_migration_offer_only_where_RunicArtifex_Tool_is_available()
    {
        using (var root = new TempDirectory())
        {
            root.Write("dotnet-tools.json", """{ "tools": { "dotnet-runic": { "version": "0.24.0" }, "Dotnet-Runic-Translations": { "version": "0.16.0" } } }""");
            var workspace = RunicWorkspace.Detect(root.Path);
            Check.Same(new[] { root.Full("dotnet-tools.json") }, workspace.ToolManifests);
            var manifests = MigrationOffer.SolutionManifests(workspace, root.Path);
            Check.Equal(root.Path, MigrationOffer.ProbeDirectory(manifests));
            // An old ID only, and no rax resolves (0.24, before the rename ships): no offer, not even the guide.
            Check.Equal(null, MigrationOffer.For(manifests), "old IDs only");
            // An old ID and a rax that resolves (here a global rax --version): the offer runs it.
            var offer = MigrationOffer.For(manifests, new MigrateRunner(root.Path, globalRax: true))!;
            Check.Same(new[] { "dotnet-runic", "dotnet-runic-translations" }, offer.FormerIds);
            Check.True(offer.Runner.GlobalRax && offer.Runner.Directory == root.Path, "the resolved global rax");
            Check.True(offer.Message.Contains("dotnet-runic → RunicArtifex.Tool (rax)"), offer.Message);
        }
        using (var root = new TempDirectory())
        {
            root.Write("web/.config/dotnet-tools.json", """{ "tools": { "RunicArtifex.Tool": { "version": "0.25.0" }, "dotnet-runic-translations": {} } }""");
            var manifests = MigrationOffer.SolutionManifests(RunicWorkspace.Detect(root.Path), root.Path);
            Check.Equal(null, MigrationOffer.ProbeDirectory(manifests), "listed: no probe");
            var offer = MigrationOffer.For(manifests)!;
            Check.Same(new[] { "dotnet-runic-translations" }, offer.FormerIds);
            Check.True(!offer.Runner.GlobalRax && offer.Runner.Directory == root.Full("web"), "dotnet rax from the manifest's folder");
        }
        using (var root = new TempDirectory())
        {
            root.Write(".config/dotnet-tools.json", """{ "tools": { "runicartifex.tool": {}, "runicartifex.translations.tool": {} } }""");
            Check.Equal(null, MigrationOffer.For(MigrationOffer.SolutionManifests(RunicWorkspace.Detect(root.Path), root.Path)), "new IDs only");
        }
        Check.Same(new[] { "rax", "migrate", "--dry-run", "--json" }, RunicTools.MigrateArguments(dryRun: true));
        Check.Same(new[] { "migrate", "--json" }, RunicTools.MigrateArguments(dryRun: false, globalRax: true));
        Check.Same(new[] { "rax", "--version" }, RunicTools.RaxVersionArguments(globalRax: false));
        Check.Equal("2 rewritten, 0 moved, 1 reported", MigrationOffer.Summary("""{ "payload": { "rewritten": [ 1, 2 ], "moved": [], "reported": [ 3 ] } }"""));
        Check.Equal("Nothing to do.", MigrationOffer.Summary("Nothing to do.\n"));
    }

    [Test]
    public void Detect_reads_the_solution_like_the_VS_Code_workspace_scan()
    {
        using var root = new TempDirectory();
        root.Write("App/App.csproj", """<Project><ItemGroup><PackageReference Include="Runic.Application.Views" /></ItemGroup></Project>""");
        root.Write("Lib/Lib.csproj", "<Project />");
        root.Write("translations/runic.json", "{}");
        root.Write("a/b/c/d/Deep.csproj", "<RunicApplicationFrontendWindowProject>true</RunicApplicationFrontendWindowProject>");
        root.Write("a/b/c/d/e/TooDeep.csproj", "<RunicApplicationFrontendWindowProject>true</RunicApplicationFrontendWindowProject>");
        root.Write("bin/Skipped.csproj", "<RunicApplicationFrontendWindowProject>true</RunicApplicationFrontendWindowProject>");
        root.Write("node_modules/pkg/runic.json", "{}");
        root.Write(".git/runic.json", "{}");
        root.Write(".config/dotnet-tools.json", TranslationsManifest);

        var workspace = RunicWorkspace.Detect(root.Path);
        Check.Same(new[] { root.Full("App/App.csproj"), root.Full("a/b/c/d/Deep.csproj") }.OrderBy(path => path, StringComparer.Ordinal), workspace.SdkProjects);
        Check.Same(new[] { root.Full("translations/runic.json") }, workspace.TranslationsProjects);
        Check.Same(new[] { root.Full(".config/dotnet-tools.json") }, workspace.ToolManifests);
        Check.True(workspace.HasSdkProject && workspace.IsTranslationsWorkspace && workspace.IsRunic, "both kinds");
    }

    [Test]
    public void Each_kind_alone_makes_a_runic_solution_and_others_do_not()
    {
        using (var root = new TempDirectory())
        {
            root.Write("src/runic.json", "{}");
            var workspace = RunicWorkspace.Detect(root.Path);
            Check.True(workspace.IsRunic && workspace.IsTranslationsWorkspace && !workspace.HasSdkProject, "runic.json only");
        }
        using (var root = new TempDirectory())
        {
            root.Write("web/.config/dotnet-tools.json", TranslationsManifest);
            Check.True(RunicWorkspace.Detect(root.Path).IsTranslationsWorkspace, "a nested tool manifest");
        }
        using (var root = new TempDirectory())
        {
            root.Write(".config/dotnet-tools.json", """{ "tools": { "dotnet-ef": {} } }""");
            root.Write("App/App.csproj", """<PackageReference Include="Runic.Translations" />""");
            Check.True(!RunicWorkspace.Detect(root.Path).IsRunic, "another tool and a Translations package reference only (the rule's build property covers that project)");
        }
        Check.True(!RunicWorkspace.Detect(Path.Combine(Path.GetTempPath(), "runic-missing-" + Guid.NewGuid())).IsRunic, "a missing directory");
    }

    [Test]
    public void The_rule_context_names_every_term_and_loads_on_the_capability_build_property_and_content_type()
    {
        Check.Equal("SdkProject | TranslationsBuild | MessageEditor", RunicActivation.RuleExpression);
        Check.Equal("SolutionHasProjectCapability:RunicApplication", RunicActivation.SdkProjectValue);
        Check.Equal("SolutionHasProjectBuildProperty:RunicTranslationsToolCommand=.", RunicActivation.TranslationsBuildValue);
        Check.Equal("ActiveEditorContentType:rmf2", RunicActivation.MessageEditorValue);
        // The language client's content type is the one the rule watches.
        string client = File.ReadAllText(Path.Combine(Paths.Extension, "LanguageClient.cs"));
        Check.True(client.Contains("[Export, Name(\"" + RunicActivation.MessageContentType + "\")"), "rmf2 content type");
        var guids = new[] { RunicActivation.RuleContext, RunicActivation.WorkspaceContext, RunicActivation.ProbePackage, RunicOutputPanes.SdkGuid, RunicOutputPanes.TranslationsGuid };
        foreach (string guid in guids) Check.True(Guid.TryParse(guid, out _), guid);
        Check.Equal(guids.Length, guids.Distinct(StringComparer.OrdinalIgnoreCase).Count(), "distinct GUIDs");
    }

    [Test]
    public void The_package_loads_only_on_the_runic_contexts_and_the_probe_on_every_solution()
    {
        string package = File.ReadAllText(Path.Combine(Paths.Extension, "RunicPackage.cs"));
        Check.True(!package.Contains("UICONTEXT."), "RunicPackage no longer autoloads on a VS context such as SolutionExists");
        Check.True(package.Contains("[ProvideUIContextRule(RunicActivation.RuleContext, RunicActivation.RuleName, RunicActivation.RuleExpression,"), "registers the rule");
        foreach (string term in new[] { "SdkProject", "TranslationsBuild", "MessageEditor" })
            Check.True(package.Contains($"RunicActivation.{term}Term") && package.Contains($"RunicActivation.{term}Value"), term);
        Check.True(package.Contains("[ProvideAutoLoad(RunicActivation.RuleContext, PackageAutoLoadFlags.BackgroundLoad)]"), "rule autoload");
        Check.True(package.Contains("[ProvideAutoLoad(RunicActivation.WorkspaceContext, PackageAutoLoadFlags.BackgroundLoad)]"), "workspace autoload");

        string probe = File.ReadAllText(Path.Combine(Paths.Extension, "Activation", "RunicWorkspaceProbe.cs"));
        Check.True(probe.Contains("[ProvideAutoLoad(VSConstants.UICONTEXT.SolutionExistsAndFullyLoaded_string, PackageAutoLoadFlags.BackgroundLoad)]"), "probe autoload");
        Check.True(probe.Contains("[Guid(RunicActivation.ProbePackage)]") && probe.Contains("RunicWorkspace.Detect(") && probe.Contains("RunicActivation.WorkspaceContext"), "probe wiring");
    }

    [Test]
    public void The_runic_menu_is_visible_in_either_context()
    {
        XNamespace vsct = "http://schemas.microsoft.com/VisualStudio/2005-10-18/CommandTable";
        var table = XDocument.Load(Path.Combine(Paths.Extension, "Commands.vsct"));
        string Guid(string name) => ((string)table.Descendants(vsct + "GuidSymbol").Single(symbol => (string?)symbol.Attribute("name") == name).Attribute("value")!).Trim('{', '}');
        var items = table.Descendants(vsct + "VisibilityItem").Select(item => ((string)item.Attribute("id")!, Guid((string)item.Attribute("context")!))).ToList();
        Check.Same(new[] { ("RunicToolsMenu", RunicActivation.RuleContext), ("RunicToolsMenu", RunicActivation.WorkspaceContext) }, items);
        var menu = table.Descendants(vsct + "Menu").Single(element => (string?)element.Attribute("id") == "RunicToolsMenu");
        Check.Same(new[] { "DynamicVisibility", "DefaultInvisible" }, menu.Elements(vsct + "CommandFlag").Select(flag => flag.Value));
    }
}

internal sealed class TempDirectory : IDisposable
{
    public TempDirectory()
    {
        Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "runic-devkit-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path);
    }

    public string Path { get; }

    public string Full(string relative) => System.IO.Path.Combine(Path, relative.Replace('/', System.IO.Path.DirectorySeparatorChar));

    public void Write(string relative, string text)
    {
        string file = Full(relative);
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(file)!);
        File.WriteAllText(file, text);
    }

    public void Dispose() => Directory.Delete(Path, recursive: true);
}
