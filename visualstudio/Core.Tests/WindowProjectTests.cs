using System;
using System.IO;
using System.Linq;
using TUnit.Core;

namespace RunicArtifex.DevKit.VisualStudio.Core.Tests;

/// <summary>
/// Only Window projects are <c>dotnet runic dev</c> targets (runic-editors#77, as VS Code's #73 and
/// <c>vscode/test/dependencies.test.ts</c>): a library or test project would stop <c>dev</c> with RAPPDEV1005. The
/// ViewModels and Doctor sections still list every Runic SDK project.
/// </summary>
public class WindowProjectTests
{
    private const string Window = """<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><OutputType>Exe</OutputType><OutputType Condition="x">WinExe</OutputType><RunicApplicationFrontendWindowProject>true</RunicApplicationFrontendWindowProject></PropertyGroup><ItemGroup><PackageReference Include="Runic.Application.Views.Desktop" Version="0.24.0" /></ItemGroup></Project>""";
    private const string ClientLibrary = """<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><RunicApplicationFrontendClientLibrary>true</RunicApplicationFrontendClientLibrary></PropertyGroup><ItemGroup><PackageReference Include="Runic.Application.Views" Version="0.24.0" /></ItemGroup></Project>""";
    private const string Library = """<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><OutputType>Library</OutputType></PropertyGroup><ItemGroup><PackageReference Include="Runic.Application.Views" Version="0.24.0" /></ItemGroup></Project>""";
    /// <summary>The template's tests: an executable (TUnit) that references Runic.Application.Testing.</summary>
    private const string TestProject = """<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><OutputType>Exe</OutputType></PropertyGroup><ItemGroup><PackageReference Include="TUnit" Version="1.73.19" /><PackageReference Include="Runic.Application.Testing" Version="0.24.0" /></ItemGroup></Project>""";

    [Test]
    public void A_Window_project_is_a_dev_target()
    {
        Check.True(RunicSdkProjects.IsWindowProject(Window), "RunicApplicationFrontendWindowProject");
        Check.True(RunicSdkProjects.IsWindowProject("""<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><OutputType>Exe</OutputType></PropertyGroup><ItemGroup><ProjectReference Include="../SharedUi/SharedUi.csproj" /><ProjectReference Include="../../../packages/dotnet/Runic.Application.Views.CsWebUi/Runic.Application.Views.CsWebUi.csproj" /></ItemGroup></Project>"""), "an executable that references Runic.Application");
        Check.True(RunicSdkProjects.IsWindowProject("""<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><OutputType> winexe </OutputType></PropertyGroup><ItemGroup><PackageReference Include="Runic.Desktop.WebView2" Version="0.24.0" /></ItemGroup></Project>"""), "WinExe, any case");
        Check.True(RunicSdkProjects.IsWindowProject("""<Project Sdk="Microsoft.NET.Sdk.Web"><ItemGroup><PackageReference Include="Runic.Desktop.Kestrel" Version="0.24.0" /></ItemGroup></Project>"""), "the Web SDK without an OutputType");
        Check.True(RunicSdkProjects.IsWindowProject(File.ReadAllText(Path.Combine(Repository(), "fixtures", "sdk-app", "App.csproj"))), "fixtures/sdk-app");
    }

    [Test]
    public void A_library_is_no_dev_target()
    {
        Check.True(!RunicSdkProjects.IsWindowProject(Library), "OutputType Library");
        Check.True(!RunicSdkProjects.IsWindowProject("""<Project Sdk="Microsoft.NET.Sdk"><ItemGroup><PackageReference Include="Runic.Application.Views" Version="0.24.0" /></ItemGroup></Project>"""), "no OutputType: a library");
        Check.True(!RunicSdkProjects.IsWindowProject("""<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><OutputType>Exe</OutputType></PropertyGroup><ItemGroup><PackageReference Include="Runic.Translations" Version="0.15.1" /></ItemGroup></Project>"""), "an executable that is no Runic SDK project");
        Check.True(RunicSdkProjects.IsSdkProject(Library), "still a Runic SDK project, for the ViewModels and Doctor sections");
    }

    [Test]
    public void A_test_project_is_no_dev_target()
    {
        Check.True(!RunicSdkProjects.IsWindowProject(TestProject), "Runic.Application.Testing and TUnit");
        Check.True(!RunicSdkProjects.IsWindowProject("""<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><OutputType>Exe</OutputType><IsTestProject>true</IsTestProject></PropertyGroup><ItemGroup><PackageReference Include="Runic.Application.Views" Version="0.24.0" /></ItemGroup></Project>"""), "IsTestProject");
        Check.True(!RunicSdkProjects.IsWindowProject("""<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><OutputType>Exe</OutputType></PropertyGroup><ItemGroup><PackageReference Include="Microsoft.NET.Test.Sdk" Version="18.0.0" /><PackageReference Include="xunit.v3" Version="3.1.0" /><PackageReference Include="Runic.Application.Views" Version="0.24.0" /></ItemGroup></Project>"""), "xunit");
    }

    [Test]
    public void A_client_library_is_no_dev_target_even_as_an_executable()
    {
        Check.True(!RunicSdkProjects.IsWindowProject(ClientLibrary), "RunicApplicationFrontendClientLibrary");
        Check.True(!RunicSdkProjects.IsWindowProject("""<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><OutputType>Exe</OutputType><RunicApplicationFrontendClientLibrary>true</RunicApplicationFrontendClientLibrary></PropertyGroup><ItemGroup><PackageReference Include="Runic.Application.Views" Version="0.24.0" /></ItemGroup></Project>"""), "with OutputType Exe");
        Check.True(!RunicSdkProjects.IsWindowProject(File.ReadAllText(Path.Combine(Repository(), "fixtures", "sdk-library", "SharedUi", "SharedUi.csproj"))), "fixtures/sdk-library");
    }

    [Test]
    public void A_solution_lists_every_project_but_dev_targets_only_its_Window_project()
    {
        string root = Path.Combine(Path.GetTempPath(), "runic-vs-window-" + Guid.NewGuid().ToString("N"));
        try
        {
            string app = Write(root, "App", Window), shared = Write(root, "SharedUi", ClientLibrary), tests = Write(root, "App.Tests", TestProject);
            var sdk = RunicSdkProjects.Find(new[] { app, shared, tests });
            Check.Same(new[] { tests, app, shared }, sdk);
            Check.Same(new[] { app }, RunicSdkProjects.WindowProjects(sdk));
            var states = sdk.Select(project => new SdkProjectState(project, RunicSdkProjects.WindowProjects(new[] { project }).Count == 1)).ToList();
            Check.Same(new[] { app }, SdkSections.DevTargets(states));
            Check.Same(new[] { tests, shared }, SdkSections.Libraries(states));

            // The only Window project is the target even when a library is the startup project or holds the active document.
            Check.Equal(app, DevTargets.Pick(SdkSections.DevTargets(states), Path.Combine(root, "SharedUi", "Clients.cs"), new[] { shared }, SdkSections.Libraries(states)));
            // F5 of the test project is no dev launch; F5 of the Window project is.
            Check.Equal<string?>(null, DevGuard.Target(new[] { tests }, SdkSections.DevTargets(states), debuggerInDesignMode: true), "a test startup project");
            Check.Equal(app, DevGuard.Target(new[] { shared, app }, SdkSections.DevTargets(states), debuggerInDesignMode: true), "the first Window startup project");

            // The Dev Session section shows the Window project alone; the ViewModels section still lists all three.
            var rows = DevSessionRows.Build(states, _ => DevSnapshot.None);
            Check.Same(new[] { "dotnet runic dev: not started" }, rows.Select(row => row.ToString()).ToArray());
            Check.Equal(3, SdkSections.ViewModels(states).Count);
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, recursive: true); }
    }

    [Test]
    public void A_solution_with_only_libraries_has_no_Window_project()
    {
        var states = new[] { new SdkProjectState("/repo/SharedUi/SharedUi.csproj", isWindow: false), new SdkProjectState("/repo/App.Tests/App.Tests.csproj", isWindow: false) };
        Check.Same(Array.Empty<string>(), SdkSections.DevTargets(states));

        var rows = DevSessionRows.Build(states, _ => DevSnapshot.None);
        Check.Same(new[] { "No Window project — SharedUi.csproj, App.Tests.csproj" }, rows.Select(row => row.ToString()).ToArray());
        Check.Equal(RowIcon.Info, rows[0].Icon);
        Check.Equal<string?>(null, rows[0].Project, "no project: the row offers no Start dotnet runic dev");
        Check.Equal(SdkSections.NoWindowProjectTooltip, rows[0].Tooltip);
        // Doctor and ViewModels keep the libraries.
        Check.Equal(2, SdkSections.ViewModels(states).Count);
        Check.Equal(2, SdkSections.DoctorTargets(states, null).Count);

        try { DevTargets.Pick(SdkSections.DevTargets(states), null, new[] { states[0].Project }, SdkSections.Libraries(states)); Check.True(false, "no exception"); }
        catch (InvalidOperationException error)
        {
            Check.Equal("This solution has no Window project that dotnet runic dev can run; SharedUi.csproj, App.Tests.csproj are library or test projects.", error.Message);
        }
        try { DevTargets.Pick(Array.Empty<string>(), null, null, new[] { states[0].Project }); Check.True(false, "no exception"); }
        catch (InvalidOperationException error)
        {
            Check.Equal("This solution has no Window project that dotnet runic dev can run; SharedUi.csproj is a library or test project.", error.Message);
        }
        Check.Equal<string?>(null, DevGuard.Target(new[] { states[0].Project }, SdkSections.DevTargets(states), debuggerInDesignMode: true), "F5 of a library is no dev launch");
    }

    private static string Write(string root, string name, string text)
    {
        string directory = Path.Combine(root, name);
        Directory.CreateDirectory(directory);
        string path = Path.Combine(directory, name + ".csproj");
        File.WriteAllText(path, text);
        return path;
    }

    private static string Repository()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory != null; directory = directory.Parent)
            if (File.Exists(Path.Combine(directory.FullName, "visualstudio", "Commands.vsct"))) return directory.FullName;
        throw new InvalidOperationException("The repository root was not found above " + AppContext.BaseDirectory);
    }
}
