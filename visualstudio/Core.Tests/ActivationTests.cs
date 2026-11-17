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
