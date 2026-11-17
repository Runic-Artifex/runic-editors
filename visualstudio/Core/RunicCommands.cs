using System;
using System.Collections.Generic;
using System.Linq;

namespace RunicArtifex.DevKit.VisualStudio.Core;

/// <summary>
/// The command IDs of <c>Commands.vsct</c> (command set <see cref="CommandSet"/>). VS-1 lays out the whole table so
/// later slices only add handler files; <c>CommandTableTests</c> keeps this class, the <c>.vsct</c> and the handlers in step.
/// </summary>
public static class RunicCommandIds
{
    /// <summary>guidRunicCommands. New with the Runic DevKit identity (0.22).</summary>
    public const string CommandSet = "95a991f7-e4e2-4ce5-ad24-454213efa92f";

    // Translations. 0x0100 and 0x0101 are the two commands of Runic MF2 Translations (Tools.RunicPreviewMessage,
    // Tools.RunicRestartLanguageServer); their canonical names stay.
    public const int PreviewMessage = 0x0100;
    public const int RestartLanguageServer = 0x0101;
    public const int ShowTranslationsOutput = 0x0102;
    public const int ExportXliff = 0x0103;
    public const int ImportXliffDryRun = 0x0104;
    public const int ImportCatalog = 0x0105;
    public const int OverrideLibraryMessage = 0x0106;
    public const int ShowTranslationStatus = 0x0107;
    public const int RefreshTranslationStatus = 0x0108;

    // Runic SDK: dotnet runic dev.
    public const int StartDev = 0x0200;
    public const int StartDevNoWindow = 0x0201;
    public const int StopDev = 0x0202;
    public const int OpenDevServer = 0x0203;
    public const int OpenSessionFile = 0x0204;
    public const int ShowSdkOutput = 0x0205;

    // Runic SDK: doctor and ViewModels.
    public const int RunDoctor = 0x0210;
    public const int RefreshViewModels = 0x0220;

    // Runic SDK: debugging.
    public const int DebugWindow = 0x0230;
    public const int AttachToPage = 0x0231;

    // The Runic tool window and settings.
    public const int ShowToolWindow = 0x0300;
    public const int OpenOptions = 0x0301;

    /// <summary>The Runic tool window's toolbar (a ToolWindowToolbar menu in the same command set).</summary>
    public const int ToolWindowToolbar = 0x1000;
}

/// <summary>The PARITY §7.2 slice that implements a command; until it lands, the command is a hidden stub.</summary>
public enum RunicSlice
{
    /// <summary>Works in this build.</summary>
    Implemented,
    /// <summary>Options page, Output panes, start-failure InfoBar, UIContext activation.</summary>
    Vs2,
    /// <summary>Dev session follower, start/stop dev, open server and session file.</summary>
    Vs3,
    /// <summary>Dev Session section and toolbar state (VS-4b); the ViewModels and Doctor sections (VS-4a) are implemented.</summary>
    Vs4,
    /// <summary>Debug the Window, attach to the page.</summary>
    Vs5,
    /// <summary>XLIFF, catalog import, library overrides, translation status.</summary>
    Vs6,
}

/// <summary>One command of the table: its <c>.vsct</c> symbol, title and owner.</summary>
public sealed class RunicCommand
{
    internal RunicCommand(int id, string symbol, string title, RunicSlice slice, string? canonicalName = null)
    {
        Id = id; Symbol = symbol; Title = title; Slice = slice; CanonicalName = canonicalName;
    }

    public int Id { get; }
    /// <summary>The IDSymbol name in <c>Commands.vsct</c>, equal to the <see cref="RunicCommandIds"/> constant.</summary>
    public string Symbol { get; }
    /// <summary>The menu text.</summary>
    public string Title { get; }
    public RunicSlice Slice { get; }
    /// <summary>A fixed DTE command name, for the commands that already had one.</summary>
    public string? CanonicalName { get; }
}

public static class RunicCommandCatalog
{
    public static IReadOnlyList<RunicCommand> All { get; } = new[]
    {
        new RunicCommand(RunicCommandIds.PreviewMessage, nameof(RunicCommandIds.PreviewMessage), "Preview Message", RunicSlice.Implemented, "Tools.RunicPreviewMessage"),
        new RunicCommand(RunicCommandIds.RestartLanguageServer, nameof(RunicCommandIds.RestartLanguageServer), "Restart Language Server", RunicSlice.Implemented, "Tools.RunicRestartLanguageServer"),
        new RunicCommand(RunicCommandIds.ShowTranslationsOutput, nameof(RunicCommandIds.ShowTranslationsOutput), "Show Translations Output", RunicSlice.Vs2),
        new RunicCommand(RunicCommandIds.ExportXliff, nameof(RunicCommandIds.ExportXliff), "Export XLIFF...", RunicSlice.Vs6),
        new RunicCommand(RunicCommandIds.ImportXliffDryRun, nameof(RunicCommandIds.ImportXliffDryRun), "Import XLIFF (Dry Run)...", RunicSlice.Vs6),
        new RunicCommand(RunicCommandIds.ImportCatalog, nameof(RunicCommandIds.ImportCatalog), "Import .resx or i18next Catalog...", RunicSlice.Vs6),
        new RunicCommand(RunicCommandIds.OverrideLibraryMessage, nameof(RunicCommandIds.OverrideLibraryMessage), "Override Library Message...", RunicSlice.Vs6),
        new RunicCommand(RunicCommandIds.ShowTranslationStatus, nameof(RunicCommandIds.ShowTranslationStatus), "Show Translation Status", RunicSlice.Vs6),
        new RunicCommand(RunicCommandIds.RefreshTranslationStatus, nameof(RunicCommandIds.RefreshTranslationStatus), "Refresh Translation Status", RunicSlice.Vs6),
        new RunicCommand(RunicCommandIds.StartDev, nameof(RunicCommandIds.StartDev), "Start dotnet runic dev", RunicSlice.Vs3),
        new RunicCommand(RunicCommandIds.StartDevNoWindow, nameof(RunicCommandIds.StartDevNoWindow), "Start dotnet runic dev --no-window", RunicSlice.Vs3),
        new RunicCommand(RunicCommandIds.StopDev, nameof(RunicCommandIds.StopDev), "Stop dotnet runic dev", RunicSlice.Vs3),
        new RunicCommand(RunicCommandIds.OpenDevServer, nameof(RunicCommandIds.OpenDevServer), "Open the Development Server", RunicSlice.Vs3),
        new RunicCommand(RunicCommandIds.OpenSessionFile, nameof(RunicCommandIds.OpenSessionFile), "Open the dev Session File", RunicSlice.Vs3),
        new RunicCommand(RunicCommandIds.ShowSdkOutput, nameof(RunicCommandIds.ShowSdkOutput), "Show Runic SDK Output", RunicSlice.Vs2),
        new RunicCommand(RunicCommandIds.RunDoctor, nameof(RunicCommandIds.RunDoctor), "Run dotnet runic doctor", RunicSlice.Implemented),
        new RunicCommand(RunicCommandIds.RefreshViewModels, nameof(RunicCommandIds.RefreshViewModels), "Refresh ViewModels", RunicSlice.Implemented),
        new RunicCommand(RunicCommandIds.DebugWindow, nameof(RunicCommandIds.DebugWindow), "Debug the Window", RunicSlice.Vs5),
        new RunicCommand(RunicCommandIds.AttachToPage, nameof(RunicCommandIds.AttachToPage), "Attach to the Page (JavaScript)", RunicSlice.Vs5),
        new RunicCommand(RunicCommandIds.ShowToolWindow, nameof(RunicCommandIds.ShowToolWindow), "Runic", RunicSlice.Implemented),
        new RunicCommand(RunicCommandIds.OpenOptions, nameof(RunicCommandIds.OpenOptions), "Options...", RunicSlice.Vs2),
    };

    public static RunicCommand Get(int id) => All.FirstOrDefault(command => command.Id == id)
        ?? throw new ArgumentOutOfRangeException(nameof(id), $"0x{id:X4} is not a Runic command.");

    /// <summary>What a stub says when it runs before its slice lands. Stubs normally stay hidden.</summary>
    public static string NotAvailableYet(int id) => $"{Get(id).Title} is not available yet in this build of Runic DevKit.";
}
