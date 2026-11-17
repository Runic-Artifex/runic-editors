using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using TUnit.Core;

namespace RunicArtifex.DevKit.VisualStudio.Core.Tests;

/// <summary>
/// Commands.vsct, <see cref="RunicCommandCatalog"/> and the handler files describe the same command table. The
/// Visual Studio project builds only on Windows, so these checks read its sources.
/// </summary>
public class CommandTableTests
{
    private static readonly XNamespace Vsct = "http://schemas.microsoft.com/VisualStudio/2005-10-18/CommandTable";
    private static readonly string Extension = FindExtensionDirectory();
    private static readonly XDocument Table = XDocument.Load(Path.Combine(Extension, "Commands.vsct"));

    private static string FindExtensionDirectory()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory != null; directory = directory.Parent)
        {
            string candidate = Path.Combine(directory.FullName, "visualstudio");
            if (File.Exists(Path.Combine(candidate, "Commands.vsct"))) return candidate;
        }
        throw new InvalidOperationException("visualstudio/Commands.vsct not found above " + AppContext.BaseDirectory);
    }

    private static XElement CommandSet() => Table.Descendants(Vsct + "GuidSymbol").Single(symbol => (string?)symbol.Attribute("name") == "guidRunicCommands");

    private static Dictionary<string, int> Symbols() => CommandSet().Elements(Vsct + "IDSymbol").ToDictionary(
        symbol => (string)symbol.Attribute("name")!, symbol => int.Parse(((string)symbol.Attribute("value")!).Substring(2), NumberStyles.HexNumber));

    private static IEnumerable<XElement> Buttons() => Table.Descendants(Vsct + "Button");

    private static IEnumerable<(string File, string Text)> Sources() => Directory.EnumerateFiles(Extension, "*.cs", SearchOption.AllDirectories)
        .Where(file => !Path.GetRelativePath(Extension, file).Split(Path.DirectorySeparatorChar).Any(part => part is "Core" or "Core.Tests" or "test" or "bin" or "obj"))
        .Select(file => (Path.GetRelativePath(Extension, file), File.ReadAllText(file)));

    [Test]
    public void The_catalog_has_unique_ids_symbols_and_titles()
    {
        Check.Equal(RunicCommandCatalog.All.Count, RunicCommandCatalog.All.Select(command => command.Id).Distinct().Count(), "duplicate ids");
        Check.Equal(RunicCommandCatalog.All.Count, RunicCommandCatalog.All.Select(command => command.Symbol).Distinct().Count(), "duplicate symbols");
        // Every RunicCommandIds constant except the toolbar is a catalog command under its own name.
        var constants = typeof(RunicCommandIds).GetFields(BindingFlags.Public | BindingFlags.Static)
            .Where(field => field.FieldType == typeof(int) && field.Name != nameof(RunicCommandIds.ToolWindowToolbar))
            .ToDictionary(field => field.Name, field => (int)field.GetValue(null)!);
        Check.Same(constants.OrderBy(pair => pair.Key), RunicCommandCatalog.All.ToDictionary(command => command.Symbol, command => command.Id).OrderBy(pair => pair.Key));
        Check.Equal("Start dotnet runic dev is not available yet in this build of Runic DevKit.", RunicCommandCatalog.NotAvailableYet(RunicCommandIds.StartDev));
    }

    [Test]
    public void The_vsct_declares_exactly_the_catalog_with_the_same_ids_and_guids()
    {
        Check.Equal(RunicCommandIds.CommandSet, ((string)CommandSet().Attribute("value")!).Trim('{', '}'));
        var package = Table.Descendants(Vsct + "GuidSymbol").Single(symbol => (string?)symbol.Attribute("name") == "guidRunicPackage");
        string packageClass = File.ReadAllText(Path.Combine(Extension, "RunicPackage.cs"));
        Check.True(packageClass.Contains($"PackageGuid = \"{((string)package.Attribute("value")!).Trim('{', '}')}\""), "RunicPackage.PackageGuid differs from guidRunicPackage");

        var symbols = Symbols();
        foreach (var command in RunicCommandCatalog.All)
            Check.True(symbols.TryGetValue(command.Symbol, out int id) && id == command.Id, $"{command.Symbol} = 0x{command.Id:X4} in Commands.vsct");
        Check.Equal(RunicCommandIds.ToolWindowToolbar, symbols[nameof(RunicCommandIds.ToolWindowToolbar)]);
        Check.Same(RunicCommandCatalog.All.Select(command => command.Symbol).OrderBy(id => id, StringComparer.Ordinal),
            Buttons().Select(button => (string)button.Attribute("id")!).OrderBy(id => id, StringComparer.Ordinal));
        Check.Equal(symbols.Count, symbols.Values.Distinct().Count(), "duplicate IDSymbol values");
    }

    [Test]
    public void Each_button_has_its_title_a_stable_canonical_name_and_hides_while_a_stub()
    {
        foreach (var command in RunicCommandCatalog.All)
        {
            var button = Buttons().Single(element => (string)element.Attribute("id")! == command.Symbol);
            var strings = button.Element(Vsct + "Strings")!;
            Check.Equal(command.Title, (string?)strings.Element(Vsct + "ButtonText"), command.Symbol);
            // Runic MF2 Translations' two commands keep their DTE names; the others are Runic.<symbol>.
            Check.Equal("." + (command.CanonicalName ?? "Runic." + command.Symbol), (string?)strings.Element(Vsct + "CanonicalName"), command.Symbol);
            var flags = button.Elements(Vsct + "CommandFlag").Select(flag => flag.Value).ToList();
            Check.True(flags.Contains("DynamicVisibility"), command.Symbol + " lacks DynamicVisibility");
            Check.Equal(command.Slice != RunicSlice.Implemented, flags.Contains("DefaultInvisible"), command.Symbol + ": a stub is DefaultInvisible");
        }
        Check.Equal("Tools.RunicPreviewMessage", RunicCommandCatalog.Get(RunicCommandIds.PreviewMessage).CanonicalName);
        Check.Equal("Tools.RunicRestartLanguageServer", RunicCommandCatalog.Get(RunicCommandIds.RestartLanguageServer).CanonicalName);
    }

    [Test]
    public void Every_command_and_group_is_placed_and_every_parent_exists()
    {
        var symbols = Symbols();
        var parents = Table.Descendants(Vsct + "Parent")
            .Where(parent => (string?)parent.Attribute("guid") == "guidRunicCommands").Select(parent => (string)parent.Attribute("id")!).ToList();
        foreach (string parent in parents) Check.True(symbols.ContainsKey(parent), parent + " is not declared");
        foreach (var group in Table.Descendants(Vsct + "Group"))
            Check.True(group.Element(Vsct + "Parent") != null, (string)group.Attribute("id")! + " has no parent");
        foreach (var command in RunicCommandCatalog.All)
            Check.True(Buttons().Single(element => (string)element.Attribute("id")! == command.Symbol).Element(Vsct + "Parent") != null, command.Symbol + " has no parent");
        // The tool window toolbar has the groups of PARITY §3, each with at least one command.
        var toolbarGroups = Table.Descendants(Vsct + "Group")
            .Where(group => (string?)group.Element(Vsct + "Parent")?.Attribute("id") == "ToolWindowToolbar").Select(group => (string)group.Attribute("id")!).ToList();
        Check.Same(new[] { "ToolbarDevGroup", "ToolbarDebugGroup", "ToolbarDoctorGroup", "ToolbarViewModelsGroup", "ToolbarTranslationsGroup", "ToolbarOutputGroup" }, toolbarGroups);
        foreach (string group in toolbarGroups) Check.True(parents.Contains(group), group + " is empty");
        // View > Other Windows > Runic opens the tool window.
        var placements = Table.Descendants(Vsct + "CommandPlacement").Where(placement => (string?)placement.Attribute("id") == nameof(RunicCommandIds.ShowToolWindow));
        Check.True(placements.Any(placement => (string?)placement.Element(Vsct + "Parent")!.Attribute("id") == "OtherWindowsGroup"), "ShowToolWindow is not in View > Other Windows");
    }

    [Test]
    public void Every_command_has_exactly_one_handler_file_and_stubs_match_the_catalog()
    {
        var handlers = Sources().SelectMany(source => Regex.Matches(source.Text, @"public override int Id => RunicCommandIds\.(\w+);")
            .Select(match => (Symbol: match.Groups[1].Value, source.File, Stub: source.Text.Contains(": NotAvailableYetHandler")))).ToList();
        foreach (var command in RunicCommandCatalog.All)
        {
            var found = handlers.Where(handler => handler.Symbol == command.Symbol).ToList();
            Check.Equal(1, found.Count, $"{command.Symbol} needs exactly one handler, found {string.Join(", ", found.Select(handler => handler.File))}");
            Check.Equal(command.Slice != RunicSlice.Implemented, found[0].Stub, $"{found[0].File}: a stub until {command.Slice}");
            Check.Equal(command.Symbol + "Command.cs", Path.GetFileName(found[0].File));
        }
        Check.Equal(RunicCommandCatalog.All.Count, handlers.Count, "handlers outside the catalog");
    }

    [Test]
    public void The_tool_window_has_the_four_sections_each_in_its_own_file()
    {
        Check.Same(new[] { "Dev Session", "ViewModels", "Doctor", "Translations" }, ToolWindowSections.All.Select(section => section.Title));
        Check.Same(new[] { RunicSection.Translations }, ToolWindowSections.All.Where(section => !section.SdkOnly).Select(section => section.Section));
        Check.Same(Enum.GetValues<RunicSection>(), ToolWindowSections.All.Select(section => section.Section));
        var sections = Sources().SelectMany(source => Regex.Matches(source.Text, @"public override RunicSection Section => RunicSection\.(\w+);")
            .Select(match => (Section: match.Groups[1].Value, source.File))).ToList();
        foreach (var info in ToolWindowSections.All)
            Check.Same(new[] { info.Section + "Section.cs" }, sections.Where(section => section.Section == info.Section.ToString()).Select(section => Path.GetFileName(section.File)));
        Check.Equal("ViewModels is not available yet in this build of Runic DevKit.", ToolWindowSections.Placeholder(ToolWindowSections.All[1]));
    }
}
