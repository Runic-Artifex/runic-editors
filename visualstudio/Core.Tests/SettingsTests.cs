using System;
using System.IO;
using TUnit.Core;

namespace RunicArtifex.DevKit.VisualStudio.Core.Tests;

/// <summary>Tools → Options → Runic DevKit and its environment-variable fallbacks.</summary>
public class SettingsTests
{
    [Test]
    public void The_dotnet_option_wins_then_RUNIC_DOTNET_then_dotnet()
    {
        Check.Equal("/opt/dotnet/dotnet", RunicSettings.Dotnet(" /opt/dotnet/dotnet ", "/env/dotnet"));
        Check.Equal("/env/dotnet", RunicSettings.Dotnet("", "/env/dotnet"));
        Check.Equal("/env/dotnet", RunicSettings.Dotnet("   ", "/env/dotnet"));
        Check.Equal("dotnet", RunicSettings.Dotnet(null, null));
        Check.Equal("dotnet", RunicSettings.Dotnet("", ""));
    }

    [Test]
    public void The_server_assembly_option_is_absolute_or_relative_to_the_solution()
    {
        string solution = Path.Combine(Path.GetTempPath(), "runic-solution");
        string absolute = Path.Combine(Path.GetTempPath(), "server", "dotnet-runic-translations.dll");

        var relative = RunicSettings.ServerAssembly(Path.Combine("..", "server", "dotnet-runic-translations.dll"), "/env/server.dll", solution)!;
        Check.Equal(Path.GetFullPath(absolute), relative.Path, "relative to the solution directory");
        Check.Equal(RunicSettings.ServerAssemblyOption, relative.Source);
        Check.Equal("Tools > Options > Runic DevKit > General > Language server assembly", relative.Source);

        Check.Equal(absolute, RunicSettings.ServerAssembly(absolute, null, null)!.Path);

        var environment = RunicSettings.ServerAssembly("", "/env/server.dll", solution)!;
        Check.Equal("/env/server.dll", environment.Path, "RUNIC_TRANSLATIONS_SERVER is used as given; the launch checks it is absolute");
        Check.Equal("RUNIC_TRANSLATIONS_SERVER", environment.Source);

        Check.True(RunicSettings.ServerAssembly(" ", null, solution) == null, "no setting runs the local tool");
    }

    [Test]
    public void A_relative_server_assembly_needs_an_open_solution()
    {
        try
        {
            RunicSettings.ServerAssembly("server.dll", null, null);
            throw new Exception("no error");
        }
        catch (InvalidOperationException error)
        {
            Check.True(error.Message.Contains("is relative (server.dll)"), error.Message);
        }
    }

    [Test]
    public void Trace_levels_match_VS_Code()
    {
        Check.Same(new[] { "Off", "Messages", "Verbose" }, Enum.GetNames<LspTraceLevel>());
        Check.Equal(LspTraceLevel.Verbose, RunicSettings.ParseTrace("verbose"));
        Check.Equal(LspTraceLevel.Messages, RunicSettings.ParseTrace(" Messages "));
        Check.Equal(LspTraceLevel.Off, RunicSettings.ParseTrace("loud"));
        Check.Equal(LspTraceLevel.Off, RunicSettings.ParseTrace("7"));
        Check.Equal(LspTraceLevel.Off, RunicSettings.ParseTrace(null));
    }

    [Test]
    public void The_options_page_has_the_three_settings_with_their_defaults()
    {
        string page = File.ReadAllText(Path.Combine(Paths.Extension, "Settings", "RunicOptionsPage.cs"));
        foreach (string property in new[] { "public string DotnetPath { get; set; } = \"\";", "public string ServerAssembly { get; set; } = \"\";", "public LspTraceLevel Trace { get; set; } = LspTraceLevel.Off;" })
            Check.True(page.Contains(property), property);
        string package = File.ReadAllText(Path.Combine(Paths.Extension, "RunicPackage.cs"));
        Check.True(package.Contains("[ProvideOptionPage(typeof(RunicOptionsPage), RunicSettings.OptionsCategory, RunicSettings.OptionsPage"), "the package registers the page");
        Check.Equal("Runic DevKit", RunicPackage_Title(package));
    }

    private static string RunicPackage_Title(string package)
    {
        const string marker = "public const string Title = \"";
        int start = package.IndexOf(marker, StringComparison.Ordinal) + marker.Length;
        return package.Substring(start, package.IndexOf('"', start) - start);
    }
}

internal static class Paths
{
    public static readonly string Extension = Find();

    private static string Find()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory != null; directory = directory.Parent)
        {
            string candidate = Path.Combine(directory.FullName, "visualstudio");
            if (File.Exists(Path.Combine(candidate, "Commands.vsct"))) return candidate;
        }
        throw new InvalidOperationException("visualstudio/Commands.vsct not found above " + AppContext.BaseDirectory);
    }
}
