using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;

namespace RunicArtifex.DevKit.VisualStudio.Core;

/// <summary>A locale of a library catalog as the application sees it (<c>runic/libraryCatalogs</c>).</summary>
public sealed class LibraryLocale
{
    public string Tag { get; internal set; } = "";
    /// <summary><c>library</c> or <c>application</c>.</summary>
    public string Origin { get; internal set; } = "";
    public string? Fallback { get; internal set; }
    public int Messages { get; internal set; }
    public int LibraryTranslated { get; internal set; }
    public int Overridden { get; internal set; }
    public int Covered { get; internal set; }
    public bool Pending { get; internal set; }
}

/// <summary>A message of a library catalog: its logical key, base text and the locales the application overrides it in.</summary>
public sealed class LibraryMessage
{
    public string Key { get; internal set; } = "";
    public string Path { get; internal set; } = "";
    public string BaseText { get; internal set; } = "";
    public IReadOnlyList<string> Inputs { get; internal set; } = Array.Empty<string>();
    public IReadOnlyList<string> Overridden { get; internal set; } = Array.Empty<string>();
    public IReadOnlyList<string> Changed { get; internal set; } = Array.Empty<string>();
}

/// <summary>An installed library catalog; <see cref="Messages"/> and <see cref="Locales"/> are present when the package that ships it is installed.</summary>
public sealed class LibraryCatalog
{
    public string Catalog { get; internal set; } = "";
    public string? Package { get; internal set; }
    public string? Version { get; internal set; }
    public string? Origin { get; internal set; }
    public bool Overridden { get; internal set; }
    public string? Problem { get; internal set; }
    public string? Directory { get; internal set; }
    public string? BaseLocale { get; internal set; }
    public IReadOnlyList<LibraryLocale>? Locales { get; internal set; }
    public IReadOnlyList<LibraryMessage>? Messages { get; internal set; }
}

/// <summary>The <c>runic/libraryCatalogs</c> answer for a project.</summary>
public sealed class LibraryCatalogList
{
    public string Project { get; internal set; } = "";
    public IReadOnlyList<LibraryCatalog> Catalogs { get; internal set; } = Array.Empty<LibraryCatalog>();
    public IReadOnlyList<string> Warnings { get; internal set; } = Array.Empty<string>();
}

/// <summary>One entry of a choice list (VS Code's quick pick item).</summary>
public sealed class Choice<T>
{
    public Choice(string label, string? description, string? detail, T value)
    {
        Label = label; Description = description; Detail = detail; Value = value;
    }

    public string Label { get; }
    public string? Description { get; }
    public string? Detail { get; }
    public T Value { get; }
    public override string ToString() => Description != null ? $"{Label} — {Description}" : Label;
}

/// <summary>An open document as far as the unsaved-changes check needs it.</summary>
public sealed class OpenDocument
{
    public OpenDocument(string path, bool isDirty) { Path = path; IsDirty = isDirty; }
    public string Path { get; }
    public bool IsDirty { get; }
}

/// <summary>
/// Override Library Message… and the read-only library documents, as VS Code's <c>vscode/src/libraries.ts</c>:
/// the choices of the <c>runic/libraryCatalogs</c> answer, the override source <c>runic-translations overrides add</c>
/// writes, and the text of a <c>runic-library:</c> URI (<c>runic/libraryDocument</c>).
/// </summary>
public static class LibraryCatalogs
{
    /// <summary>The URI scheme of the read-only library sources that Go to Definition opens from an override source.</summary>
    public const string Scheme = "runic-library";
    public const string NoCatalog = "No installed package packs a library catalog. Restore the project (dotnet restore, npm install) first.";

    /// <summary>Reads the JSON of a <c>runic/libraryCatalogs</c> answer.</summary>
    public static LibraryCatalogList Parse(string json)
    {
        var result = Json.Object(Json.Parse(json)) ?? throw new FormatException("runic/libraryCatalogs returned no object.");
        return new LibraryCatalogList
        {
            Project = Json.String(result, "project") ?? "",
            Warnings = Strings(Json.Get(result, "warnings")),
            Catalogs = (Json.Array(Json.Get(result, "catalogs")) ?? new List<object?>()).Select(Json.Object).Where(item => item != null).Select(item => new LibraryCatalog
            {
                Catalog = Json.String(item, "catalog") ?? "",
                Package = Json.String(item, "package"),
                Version = Json.String(item, "version"),
                Origin = Json.String(item, "origin"),
                Overridden = Json.Boolean(item, "overridden") ?? false,
                Problem = Json.String(item, "problem"),
                Directory = Json.String(item, "directory"),
                BaseLocale = Json.String(item, "baseLocale"),
                Locales = Json.Array(Json.Get(item, "locales"))?.Select(Json.Object).Where(locale => locale != null).Select(locale => new LibraryLocale
                {
                    Tag = Json.String(locale, "tag") ?? "",
                    Origin = Json.String(locale, "origin") ?? "",
                    Fallback = Json.String(locale, "fallback"),
                    Messages = (int)(Json.Number(locale, "messages") ?? 0),
                    LibraryTranslated = (int)(Json.Number(locale, "libraryTranslated") ?? 0),
                    Overridden = (int)(Json.Number(locale, "overridden") ?? 0),
                    Covered = (int)(Json.Number(locale, "covered") ?? 0),
                    Pending = Json.Boolean(locale, "pending") ?? false,
                }).ToList(),
                Messages = Json.Array(Json.Get(item, "messages"))?.Select(Json.Object).Where(message => message != null).Select(message => new LibraryMessage
                {
                    Key = Json.String(message, "key") ?? "",
                    Path = Json.String(message, "path") ?? "",
                    BaseText = Json.String(message, "baseText") ?? "",
                    Inputs = Strings(Json.Get(message, "inputs")),
                    Overridden = Strings(Json.Get(message, "overridden")),
                    Changed = Strings(Json.Get(message, "changed")),
                }).ToList(),
            }).ToList(),
        };
    }

    private static IReadOnlyList<string> Strings(object? value) => Json.Array(value)?.OfType<string>().ToList() ?? (IReadOnlyList<string>)Array.Empty<string>();

    /// <summary>The catalogs to choose from: installed ones first, overridden before the others.</summary>
    public static IReadOnlyList<Choice<LibraryCatalog>> CatalogChoices(LibraryCatalogList result)
        => result.Catalogs.Where(catalog => catalog.Messages != null)
            .OrderBy(catalog => catalog.Overridden ? 0 : 1).ThenBy(catalog => catalog.Catalog, StringComparer.Ordinal)
            .Select(catalog =>
            {
                string? description = catalog.Package != null ? $"{catalog.Package} {catalog.Version ?? ""}".Trim() : null;
                var locales = OverriddenLocales(catalog);
                string detail = catalog.Overridden
                    ? $"Overridden in {(locales.Count > 0 ? string.Join(", ", locales) : "no locale yet")}"
                    : "Not overridden yet: runic.json gets an \"overrides\" entry";
                return new Choice<LibraryCatalog>(catalog.Catalog, description, detail, catalog);
            }).ToList();

    private static IReadOnlyList<string> OverriddenLocales(LibraryCatalog catalog)
        => (catalog.Messages ?? Array.Empty<LibraryMessage>()).SelectMany(message => message.Overridden).Distinct().OrderBy(tag => tag, StringComparer.Ordinal).ToList();

    /// <summary>The messages of a catalog with their base text; overridden and changed ones say so.</summary>
    public static IReadOnlyList<Choice<LibraryMessage>> MessageChoices(LibraryCatalog catalog)
        => (catalog.Messages ?? Array.Empty<LibraryMessage>()).Select(message =>
        {
            var details = new List<string>();
            if (message.Overridden.Count > 0) details.Add("Overridden in " + string.Join(", ", message.Overridden));
            if (message.Changed.Count > 0) details.Add("library text changed since the override in " + string.Join(", ", message.Changed));
            if (message.Inputs.Count > 0) details.Add("Inputs: " + string.Join(", ", message.Inputs));
            return new Choice<LibraryMessage>(message.Path, message.BaseText.Split('\n')[0], details.Count > 0 ? string.Join("; ", details) : null, message);
        }).ToList();

    /// <summary>The label of the choice that asks for a locale the library lacks.</summary>
    public const string AnotherLocale = "Another locale…";

    /// <summary>
    /// The locales to override a message in: the library's, then those the application adds, with coverage and fallback,
    /// and last <see cref="AnotherLocale"/> (an empty value).
    /// </summary>
    public static IReadOnlyList<Choice<string>> LocaleChoices(LibraryCatalog catalog, LibraryMessage? message)
        => (catalog.Locales ?? Array.Empty<LibraryLocale>()).Select(locale => new Choice<string>(locale.Tag,
                locale.Origin == "library"
                    ? "library" + (locale.Tag == catalog.BaseLocale ? " base locale" : "")
                    : $"added by the application, falls back to {locale.Fallback ?? catalog.BaseLocale}",
                $"{locale.Covered}/{locale.Messages} messages in its own text" + (message != null && IsOverriddenIn(message, locale.Tag) ? "; this message is overridden here" : ""),
                locale.Tag))
            .Concat(new[] { new Choice<string>(AnotherLocale, "a locale the library lacks", null, "") }).ToList();

    /// <summary>Whether the application already overrides <paramref name="message"/> in <paramref name="locale"/>; locale tags compare case-insensitively.</summary>
    public static bool IsOverriddenIn(LibraryMessage message, string locale)
        => message.Overridden.Any(tag => string.Equals(tag, locale, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// The canonical BCP 47 form of a typed locale tag (<c>de_ch</c> and <c>DE-ch</c> become <c>de-CH</c>), as the CLI
    /// names override sources; null when it is not a well-formed tag. Casing follows RFC 5646: language and extensions
    /// lower case, script title case, region upper case.
    /// </summary>
    public static string? CanonicalLocale(string? input)
    {
        string value = (input ?? "").Trim().Replace('_', '-');
        if (value.Length == 0) return null;
        string[] subtags = value.Split('-');
        if (subtags.Any(subtag => subtag.Length == 0 || subtag.Length > 8 || !subtag.All(IsAsciiAlphanumeric))) return null;
        int index = 0;
        var result = new List<string>();
        string language = subtags[index];
        if (!(language.Length is 2 or 3 or (>= 5 and <= 8)) || !language.All(IsAsciiLetter)) return null;
        result.Add(language.ToLowerInvariant()); index++;
        if (index < subtags.Length && subtags[index].Length == 4 && subtags[index].All(IsAsciiLetter))
        {
            string script = subtags[index++];
            result.Add(char.ToUpperInvariant(script[0]) + script.Substring(1).ToLowerInvariant());
        }
        if (index < subtags.Length && ((subtags[index].Length == 2 && subtags[index].All(IsAsciiLetter)) || (subtags[index].Length == 3 && subtags[index].All(char.IsDigit))))
            result.Add(subtags[index++].ToUpperInvariant());
        var variants = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        while (index < subtags.Length && (subtags[index].Length >= 5 || (subtags[index].Length == 4 && char.IsDigit(subtags[index][0]))))
        {
            if (!variants.Add(subtags[index])) return null;
            result.Add(subtags[index++].ToLowerInvariant());
        }
        var singletons = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        while (index < subtags.Length)
        {
            string singleton = subtags[index++];
            if (singleton.Length != 1) return null;
            bool privateUse = string.Equals(singleton, "x", StringComparison.OrdinalIgnoreCase);
            if (!privateUse && !singletons.Add(singleton)) return null;
            int start = index;
            while (index < subtags.Length && (privateUse || subtags[index].Length >= 2)) index++;
            if (index == start || subtags.Skip(start).Take(index - start).Any(subtag => !privateUse && subtag.Length < 2)) return null;
            result.Add(singleton.ToLowerInvariant());
            result.AddRange(subtags.Skip(start).Take(index - start).Select(subtag => subtag.ToLowerInvariant()));
        }
        return string.Join("-", result);
    }

    private static bool IsAsciiLetter(char character) => character is >= 'a' and <= 'z' or >= 'A' and <= 'Z';
    private static bool IsAsciiAlphanumeric(char character) => IsAsciiLetter(character) || character is >= '0' and <= '9';

    /// <summary>The override source of a locale: <c>overrides/&lt;catalog&gt;/&lt;locale&gt;.rmf2</c> beside runic.json.</summary>
    public static string OverrideSourcePath(string manifest, string catalog, string locale)
        => System.IO.Path.Combine(System.IO.Path.GetDirectoryName(manifest) ?? "", "overrides", catalog, locale + ".rmf2");

    /// <summary>The 0-based line of a message's name in an RMF2 source (its last path segment inside its groups), or 0.</summary>
    public static int MessageLine(string text, string path)
    {
        string[] segments = path.Split('.');
        string[] lines = text.Split('\n');
        int depth = 0;
        for (int index = 0; index < lines.Length; index++)
        {
            string line = lines[index].Trim();
            if (line.StartsWith("#", StringComparison.Ordinal)) continue;
            var match = System.Text.RegularExpressions.Regex.Match(line, @"^([A-Za-z_][A-Za-z0-9_]*)\s*([={])");
            if (!match.Success || depth >= segments.Length || match.Groups[1].Value != segments[depth]) continue;
            if (depth == segments.Length - 1 && match.Groups[2].Value == "=") return index;
            if (match.Groups[2].Value == "{") depth++;
        }
        return 0;
    }

    /// <summary>
    /// The open documents with unsaved changes among <paramref name="paths"/>, which a CLI writing those files would
    /// overwrite underneath the editor. Paths compare case-insensitively when <paramref name="ignoreCase"/> (Windows).
    /// </summary>
    public static IReadOnlyList<OpenDocument> Unsaved(IEnumerable<OpenDocument> documents, IEnumerable<string> paths, bool ignoreCase)
    {
        var wanted = new HashSet<string>(paths, ignoreCase ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
        return documents.Where(document => document.IsDirty && wanted.Contains(document.Path)).ToList();
    }

    /// <summary>The application's runic.json (a file URI) that a <c>runic-library:</c> URI names in its <c>project</c> query parameter.</summary>
    public static string? DocumentProject(string query)
    {
        // The query as the server wrote it (project=<encoded file URI>), or with its "=" encoded as well by a URI round trip.
        foreach (var (candidate, decode) in new[] { (query, true), (SafeDecode(query), false) })
            foreach (string part in candidate.Split('&'))
            {
                int separator = part.IndexOf('=');
                if (separator > 0 && part.Substring(0, separator) == "project")
                    return decode ? SafeDecode(part.Substring(separator + 1)) : part.Substring(separator + 1);
            }
        return null;
    }

    private static string SafeDecode(string value)
    {
        try { return Uri.UnescapeDataString(value); }
        catch (UriFormatException) { return value; }
    }

    /// <summary>True for a <c>runic-library:</c> URI.</summary>
    public static bool IsLibraryUri(string? uri) => uri != null && uri.StartsWith(Scheme + ":", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Where Visual Studio, whose LSP client opens only files, shows the library source of a
    /// <c>runic-library:/&lt;catalog&gt;/&lt;path&gt;?project=…</c> URI: a read-only copy under <paramref name="root"/>, in a
    /// directory per application project, keeping the source's file name so the editor and the title read the same.
    /// Null when the URI is not a well-formed library URI.
    /// </summary>
    public static string? DocumentPath(string root, string uri)
    {
        if (!IsLibraryUri(uri)) return null;
        string rest = uri.Substring(Scheme.Length + 1);
        int query = rest.IndexOf('?');
        string path = query >= 0 ? rest.Substring(0, query) : rest;
        string project = (query >= 0 ? DocumentProject(rest.Substring(query + 1)) : null) ?? "";
        string[] segments = path.TrimStart('/').Split('/').Select(SafeDecode).ToArray();
        if (segments.Length < 2) return null;
        var invalid = System.IO.Path.GetInvalidFileNameChars().Concat(new[] { '\\', '/', ':' }).ToArray();
        if (segments.Any(segment => segment.Length == 0 || segment == "." || segment == ".." || segment.IndexOfAny(invalid) >= 0)) return null;
        string name = segments.Last();
        if (!name.EndsWith(".rmf2", StringComparison.OrdinalIgnoreCase) && !name.EndsWith(".mf2", StringComparison.OrdinalIgnoreCase)) return null;
        return System.IO.Path.Combine(new[] { root, Hash(project) }.Concat(segments).ToArray());
    }

    private static string Hash(string value)
    {
        using var sha = SHA256.Create();
        return string.Concat(sha.ComputeHash(Encoding.UTF8.GetBytes(value)).Take(6).Select(part => part.ToString("x2")));
    }
}
