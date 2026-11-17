using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;

namespace RunicArtifex.DevKit.VisualStudio.Core;

/// <summary>One locale of <c>runic-translations status</c> (<c>runic.translations.status/1</c>).</summary>
public sealed class StatusLocale
{
    public string Locale { get; internal set; } = "";
    public string? FallbackLocale { get; internal set; }
    public int Translated { get; internal set; }
    public int Fallback { get; internal set; }
    public int Missing { get; internal set; }
    public double Coverage { get; internal set; }
    public int? Stale { get; internal set; }
    public double? MinimumCoverage { get; internal set; }
    public bool BelowThreshold { get; internal set; }
    /// <summary>The locale's effective <c>validation.translationCompleteness</c>; absent for the base locale and from older tools.</summary>
    public string? Completeness { get; internal set; }
}

public sealed class StatusProject
{
    public string Project { get; internal set; } = "";
    public string Catalog { get; internal set; } = "";
    public string BaseLocale { get; internal set; } = "";
    public int Messages { get; internal set; }
    public IReadOnlyList<StatusLocale> Locales { get; internal set; } = Array.Empty<StatusLocale>();
    public string? ReviewFile { get; internal set; }
}

public sealed class StatusResult
{
    public IReadOnlyList<StatusProject> Projects { get; internal set; } = Array.Empty<StatusProject>();
    public bool BelowThreshold { get; internal set; }
}

/// <summary>What the Translations section knows about one runic.json project of the solution.</summary>
public sealed class TranslationsProjectState
{
    public TranslationsProjectState(string manifest) { Manifest = manifest; }

    /// <summary>The project's runic.json.</summary>
    public string Manifest { get; }
    public StatusResult? Result { get; set; }
    /// <summary>Why the last status run failed; null after a success.</summary>
    public string? Error { get; set; }
    public bool Running { get; set; }
    /// <summary>True once status has been run, successfully or not.</summary>
    public bool Loaded { get; set; }
}

/// <summary>
/// <c>runic-translations status --format json</c> as VS Code reads and shows it (<c>vscode/src/status.ts</c>, the
/// Translations view of <c>sdk.ts</c> and the Translation Status panel): the same numbers, thresholds and per-locale
/// completeness policy as the command line and the Translations Editor.
/// </summary>
public static class TranslationStatus
{
    public const string PayloadType = "runic.translations.status/1";
    public const string NoProject = "This solution has no Runic Translations project (runic.json).";
    public const string Welcome = "No translation status yet. Restore the project's runic-translations tool (dotnet tool restore).";
    public const string Unavailable = "Status unavailable until the catalog compiles and the runic-translations tool is restored";

    /// <summary>The payload of the status envelope. A failing envelope throws with its fault message.</summary>
    public static StatusResult Parse(string stdout)
    {
        Dictionary<string, object?>? envelope;
        try { envelope = Json.Object(Json.Parse(stdout)); }
        catch (FormatException) { envelope = null; }
        var payload = Json.Object(Json.Get(envelope, "payload"));
        if (envelope == null || Json.String(envelope, "payloadType") != PayloadType || payload == null)
            throw new InvalidOperationException(Json.String(Json.Object(Json.Get(envelope, "fault")), "message") ?? "runic-translations status returned no status report.");
        if (Json.Array(Json.Get(payload, "projects")) is not { } projects) throw new InvalidOperationException("runic-translations status returned an invalid status report.");
        return new StatusResult
        {
            BelowThreshold = Json.Boolean(payload, "belowThreshold") ?? false,
            Projects = projects.Select(Json.Object).Where(project => project != null).Select(project => new StatusProject
            {
                Project = Json.String(project, "project") ?? "",
                Catalog = Json.String(project, "catalog") ?? "",
                BaseLocale = Json.String(project, "baseLocale") ?? "",
                Messages = (int)(Json.Number(project, "messages") ?? 0),
                ReviewFile = Json.String(project, "reviewFile"),
                Locales = (Json.Array(Json.Get(project, "locales")) ?? new List<object?>()).Select(Json.Object).Where(locale => locale != null).Select(locale => new StatusLocale
                {
                    Locale = Json.String(locale, "locale") ?? "",
                    FallbackLocale = Json.String(locale, "fallbackLocale"),
                    Translated = (int)(Json.Number(locale, "translated") ?? 0),
                    Fallback = (int)(Json.Number(locale, "fallback") ?? 0),
                    Missing = (int)(Json.Number(locale, "missing") ?? 0),
                    Coverage = Json.Number(locale, "coverage") ?? 0,
                    Stale = Json.Number(locale, "stale") is { } stale ? (int)stale : null,
                    MinimumCoverage = Json.Number(locale, "minimumCoverage"),
                    BelowThreshold = Json.Boolean(locale, "belowThreshold") ?? false,
                    Completeness = Json.String(locale, "completeness"),
                }).ToList(),
            }).ToList(),
        };
    }

    /// <summary>The status of <paramref name="locale"/> in the report's first project; matched case-insensitively, as locale tags are.</summary>
    public static StatusLocale? LocaleStatus(StatusResult? result, string? locale)
        => result == null || string.IsNullOrEmpty(locale) ? null
        : result.Projects.FirstOrDefault()?.Locales.FirstOrDefault(item => string.Equals(item.Locale, locale, StringComparison.OrdinalIgnoreCase));

    /// <summary><c>87.5%</c>, <c>90%</c>: at most two decimals, without trailing zeros.</summary>
    public static string Percent(double value)
    {
        if (value == Math.Floor(value)) return value.ToString("0", CultureInfo.InvariantCulture) + "%";
        return value.ToString("0.##", CultureInfo.InvariantCulture) + "%";
    }

    /// <summary><c>Coverage de-CH: 87.5% (30 translated, 5 from de, 5 missing, 1 stale), below its minimum 90%; missing translations are warnings</c>.</summary>
    public static string CoverageLine(StatusLocale locale)
    {
        var parts = new List<string> { $"{locale.Translated} translated" };
        if (locale.Fallback != 0) parts.Add($"{locale.Fallback} from {locale.FallbackLocale ?? "a fallback locale"}");
        parts.Add($"{locale.Missing} missing");
        if (locale.Stale != null) parts.Add($"{locale.Stale} stale");
        string line = $"Coverage {locale.Locale}: {Percent(locale.Coverage)} ({string.Join(", ", parts)})";
        if (locale.MinimumCoverage is { } minimum) line += locale.BelowThreshold ? $", below its minimum {Percent(minimum)}" : $", minimum {Percent(minimum)}";
        if (!string.IsNullOrEmpty(locale.Completeness)) line += $"; missing translations are {(locale.Completeness == "allow" ? "allowed" : locale.Completeness + "s")}";
        return line;
    }

    /// <summary>
    /// The Translations section (VS Code's Translations view): a row per locale with its coverage; with several projects,
    /// one row per project holding its locales. Rows carry the project's runic.json.
    /// </summary>
    public static IReadOnlyList<TreeRow> Rows(IReadOnlyList<TranslationsProjectState> projects, string? solutionDirectory)
    {
        if (projects.Count == 0) return Array.Empty<TreeRow>();
        if (projects.All(project => !project.Loaded && !project.Running)) return Array.Empty<TreeRow>();
        if (projects.Count == 1) return ProjectRows(projects[0]);
        return projects.Select(project =>
        {
            var row = new TreeRow(ProjectLabel(project.Manifest, solutionDirectory), RowIcon.Project, project.Manifest)
            {
                Tooltip = project.Manifest, Expanded = true,
                Description = project.Result?.Projects.FirstOrDefault()?.Catalog,
            };
            row.Children.AddRange(ProjectRows(project));
            return row;
        }).ToList();
    }

    private static IReadOnlyList<TreeRow> ProjectRows(TranslationsProjectState project)
    {
        if (project.Running && project.Result == null) return new[] { new TreeRow("Reading translation status…", RowIcon.Running, project.Manifest) };
        if (project.Error != null) return new[] { new TreeRow("Status unavailable: " + project.Error, RowIcon.Warning, project.Manifest) { Tooltip = Unavailable + "." } };
        if (project.Result == null) return new[] { new TreeRow(Unavailable, RowIcon.Warning, project.Manifest) };
        return project.Result.Projects.SelectMany(entry => entry.Locales.Select(locale => new TreeRow(locale.Locale,
            locale.BelowThreshold ? RowIcon.Warning : locale.Missing != 0 ? RowIcon.NotRun : RowIcon.Pass, project.Manifest)
        {
            Description = (locale.Coverage == Math.Floor(locale.Coverage) ? locale.Coverage.ToString("0", CultureInfo.InvariantCulture) : locale.Coverage.ToString("0.0", CultureInfo.InvariantCulture))
                + "%" + (locale.Locale == entry.BaseLocale ? " (base)" : ""),
            Tooltip = CoverageLine(locale),
        })).ToList();
    }

    /// <summary>The project's directory relative to the solution directory, or its directory name.</summary>
    public static string ProjectLabel(string manifest, string? solutionDirectory)
    {
        string directory = Path.GetDirectoryName(manifest) ?? manifest;
        if (!string.IsNullOrEmpty(solutionDirectory) && TranslationsTool.IsWithin(Path.GetFullPath(solutionDirectory), Path.GetFullPath(directory)))
        {
            string relative = Path.GetFullPath(directory).Substring(Path.GetFullPath(solutionDirectory).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar).Length).TrimStart(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            if (relative.Length > 0) return relative.Replace('\\', '/');
        }
        return Path.GetFileName(directory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
    }

    /// <summary>The document title of Show Translation Status.</summary>
    public static string Title(StatusResult result) => "Translation Status · " + (result.Projects.FirstOrDefault()?.Catalog ?? "");

    /// <summary>
    /// The Show Translation Status document (VS Code's panel): one Markdown table per project. Visual Studio opens it
    /// read-only with its Markdown preview.
    /// </summary>
    public static string Markdown(StatusResult result)
    {
        var text = new StringBuilder("# Translation status\n");
        foreach (var project in result.Projects)
        {
            bool stale = project.ReviewFile != null;
            text.Append("\n## ").Append(Cell(project.Catalog)).Append("\n\n")
                .Append(Cell(project.Project)).Append(" · ").Append(project.Messages).Append(" message(s) · base locale ").Append(Cell(project.BaseLocale));
            if (stale) text.Append(" · review file ").Append(Cell(project.ReviewFile!));
            text.Append("\n\n| Locale | Translated | Fallback | Missing |").Append(stale ? " Stale |" : "").Append(" Coverage | Missing translations |\n")
                .Append("| --- | ---: | ---: | ---: |").Append(stale ? " ---: |" : "").Append(" ---: | --- |\n");
            foreach (var locale in project.Locales)
            {
                string name = locale.Locale + (locale.Locale == project.BaseLocale ? " (base)" : locale.FallbackLocale != null ? " → " + locale.FallbackLocale : "");
                string coverage = Percent(locale.Coverage) + (locale.MinimumCoverage is { } minimum ? $" (min {Percent(minimum)})" : "");
                if (locale.BelowThreshold) { name = "**" + Cell(name) + "** ⚠"; coverage = "**" + coverage + "** below minimum"; }
                else name = Cell(name);
                text.Append("| ").Append(name).Append(" | ").Append(locale.Translated).Append(" | ").Append(locale.Fallback).Append(" | ").Append(locale.Missing).Append(" |");
                if (stale) text.Append(' ').Append(locale.Stale?.ToString(CultureInfo.InvariantCulture) ?? "–").Append(" |");
                text.Append(' ').Append(Cell(coverage)).Append(" | ").Append(Cell(locale.Completeness ?? (locale.Locale == project.BaseLocale ? "–" : ""))).Append(" |\n");
            }
        }
        return text.Append("\nFrom runic-translations status, the same report as the command line and the Translations Editor.\n").ToString();
    }

    private static string Cell(string value) => value.Replace("\\", "\\\\").Replace("|", "\\|").Replace("\r", " ").Replace("\n", " ").Replace("<", "&lt;");
}
