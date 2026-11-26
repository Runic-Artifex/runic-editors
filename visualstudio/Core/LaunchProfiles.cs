using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml;

namespace RunicArtifex.DevKit.VisualStudio.Core;

/// <summary>
/// A profile of <c>Properties/launchSettings.json</c>, with the standard keys Debug the Window reads
/// (design/ide-run-debug §3.2): <c>commandName</c>, <c>commandLineArgs</c>, <c>environmentVariables</c> and
/// <c>workingDirectory</c>.
/// </summary>
public sealed class LaunchProfile
{
    public LaunchProfile(string name, string? commandName, string? commandLineArgs, IReadOnlyList<KeyValuePair<string, string>> environmentVariables, string? workingDirectory)
    {
        Name = name; CommandName = commandName; CommandLineArgs = commandLineArgs; EnvironmentVariables = environmentVariables; WorkingDirectory = workingDirectory;
    }

    public string Name { get; }
    public string? CommandName { get; }
    /// <summary>The profile's arguments, one command line, as written.</summary>
    public string? CommandLineArgs { get; }
    /// <summary>The profile's variables in the file's order.</summary>
    public IReadOnlyList<KeyValuePair<string, string>> EnvironmentVariables { get; }
    /// <summary>As written; relative to the project folder, as Visual Studio resolves it.</summary>
    public string? WorkingDirectory { get; }
    /// <summary>Only <c>Project</c> profiles start the project's own program; Debug the Window applies only those.</summary>
    public bool IsProject => string.Equals(CommandName, LaunchProfiles.ProjectCommand, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// The profile with <c>%NAME%</c> replaced by the variables of <paramref name="environment"/>, as Visual Studio's
    /// project system replaces them in a profile's values; unknown names stay as written. MSBuild <c>$(Property)</c>
    /// tokens, which Visual Studio also replaces, stay as written.
    /// </summary>
    public LaunchProfile Expand(IReadOnlyDictionary<string, string> environment)
        => new(Name, CommandName, LaunchProfiles.ExpandVariables(CommandLineArgs, environment),
            EnvironmentVariables.Select(pair => new KeyValuePair<string, string>(pair.Key, LaunchProfiles.ExpandVariables(pair.Value, environment)!)).ToList(),
            LaunchProfiles.ExpandVariables(WorkingDirectory, environment));
}

/// <summary>Which profile is active, and why none applies (for the Output pane).</summary>
public sealed class ActiveLaunchProfile
{
    public ActiveLaunchProfile(LaunchProfile? profile, string? note) { Profile = profile; Note = note; }

    /// <summary>The <c>Project</c> profile Debug the Window applies, or null.</summary>
    public LaunchProfile? Profile { get; }
    /// <summary>Why no profile applies when the file names one, or null.</summary>
    public string? Note { get; }
}

/// <summary>
/// Reads <c>Properties/launchSettings.json</c> and chooses the active profile, as Visual Studio's F5 does: the user
/// file's <c>ActiveDebugProfile</c>, else the first profile (design/ide-run-debug §3.6, B4).
/// </summary>
public static class LaunchProfiles
{
    public const string ProjectCommand = "Project";
    /// <summary>The project's user-file property that holds the toolbar's launch profile.</summary>
    public const string ActiveDebugProfileProperty = "ActiveDebugProfile";

    private static readonly Regex Variable = new("%([^%\\s]+)%", RegexOptions.CultureInvariant);

    /// <summary><c>&lt;project folder&gt;/Properties/launchSettings.json</c>.</summary>
    public static string SettingsPath(string project)
        => Path.Combine(Path.GetDirectoryName(project) ?? "", "Properties", "launchSettings.json");

    /// <summary>
    /// The profiles of a <c>launchSettings.json</c> in the file's order. Comments and trailing commas are allowed, as
    /// Visual Studio allows them. Throws <see cref="FormatException"/> for a file that is not a JSON object.
    /// </summary>
    public static IReadOnlyList<LaunchProfile> Parse(string text)
    {
        var root = Json.Object(Json.Parse(Relax(text))) ?? throw new FormatException("launchSettings.json is not a JSON object.");
        var profiles = Json.Object(Json.Get(root, "profiles"));
        if (profiles == null) return Array.Empty<LaunchProfile>();
        var result = new List<LaunchProfile>();
        // Json reads objects into a Dictionary filled in order and never removed from, so it enumerates in the file's order.
        foreach (var pair in profiles)
        {
            if (Json.Object(pair.Value) is not { } profile) continue;
            var variables = new List<KeyValuePair<string, string>>();
            if (Json.Object(Json.Get(profile, "environmentVariables")) is { } environment)
                foreach (var variable in environment)
                    if (variable.Key.Length > 0 && Scalar(variable.Value) is { } value) variables.Add(new KeyValuePair<string, string>(variable.Key, value));
            result.Add(new LaunchProfile(pair.Key, Json.String(profile, "commandName"), Scalar(Json.Get(profile, "commandLineArgs")), variables, Scalar(Json.Get(profile, "workingDirectory"))));
        }
        return result;
    }

    /// <summary>
    /// The profile Visual Studio's toolbar shows: the one named <paramref name="activeName"/>, else the first. A profile
    /// of another kind than <c>Project</c> (<c>Executable</c>, <c>IISExpress</c>, …) applies nothing.
    /// </summary>
    public static ActiveLaunchProfile Choose(IReadOnlyList<LaunchProfile> profiles, string? activeName)
    {
        if (profiles.Count == 0) return new ActiveLaunchProfile(null, null);
        string name = (activeName ?? "").Trim();
        var profile = name.Length == 0 ? null
            : profiles.FirstOrDefault(candidate => string.Equals(candidate.Name, name, StringComparison.Ordinal))
              ?? profiles.FirstOrDefault(candidate => string.Equals(candidate.Name, name, StringComparison.OrdinalIgnoreCase));
        profile ??= profiles[0];
        return profile.IsProject ? new ActiveLaunchProfile(profile, null)
            : new ActiveLaunchProfile(null, $"the active launch profile {profile.Name} is a {profile.CommandName ?? "(no commandName)"} profile, so Debug the Window applies no profile; only Project profiles apply");
    }

    /// <summary>
    /// The active <c>Project</c> profile of <paramref name="project"/>: <see cref="SettingsPath"/> read and
    /// <see cref="Choose"/>n. No file means no profile, as before; an unreadable file is an error that names it.
    /// </summary>
    public static ActiveLaunchProfile Read(string project, string? activeName)
    {
        string path = SettingsPath(project);
        string text;
        try { text = File.ReadAllText(path); }
        catch (Exception error) when (error is FileNotFoundException or DirectoryNotFoundException) { return new ActiveLaunchProfile(null, null); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { throw new InvalidOperationException($"Could not read {path}: {error.Message}", error); }
        try { return Choose(Parse(text), activeName); }
        catch (FormatException error) { throw new InvalidOperationException($"{path} is not valid JSON: {error.Message}", error); }
    }

    /// <summary>
    /// <c>ActiveDebugProfile</c> from the project's user file (<c>&lt;project&gt;.user</c>), for when Visual Studio's
    /// project storage does not answer; null when it has none.
    /// </summary>
    public static string? ActiveNameFromUserFile(string project)
    {
        string path = project + ".user";
        if (!File.Exists(path)) return null;
        try
        {
            var document = new XmlDocument { XmlResolver = null };
            using (var reader = XmlReader.Create(path, new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null }))
                document.Load(reader);
            foreach (XmlNode node in document.GetElementsByTagName(ActiveDebugProfileProperty))
                if (node.InnerText.Trim() is { Length: > 0 } name) return name;
            return null;
        }
        catch (Exception error) when (error is XmlException or IOException or UnauthorizedAccessException) { return null; }
    }

    /// <summary><c>%NAME%</c> replaced by <paramref name="environment"/>'s value (names compare without case, as on Windows).</summary>
    public static string? ExpandVariables(string? text, IReadOnlyDictionary<string, string> environment)
    {
        if (string.IsNullOrEmpty(text) || text!.IndexOf('%') < 0) return text;
        var lookup = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var pair in environment) lookup[pair.Key] = pair.Value;
        return Variable.Replace(text, match => lookup.TryGetValue(match.Groups[1].Value, out string? value) ? value : match.Value);
    }

    /// <summary>A string, number or Boolean as text (Visual Studio accepts <c>"PORT": 5000</c>); null for the rest.</summary>
    private static string? Scalar(object? value) => value switch
    {
        string text => text,
        double number => number.ToString(System.Globalization.CultureInfo.InvariantCulture),
        bool flag => flag ? "true" : "false",
        _ => null,
    };

    /// <summary>The text without <c>//</c> and <c>/* */</c> comments and without trailing commas, outside strings.</summary>
    internal static string Relax(string text)
    {
        var result = new StringBuilder(text.Length);
        int pendingComma = -1; // the index in result of a comma that may be trailing
        for (int i = 0; i < text.Length; i++)
        {
            char c = text[i];
            if (c == '"')
            {
                pendingComma = -1;
                int start = i++;
                while (i < text.Length && text[i] != '"') i += text[i] == '\\' ? 2 : 1;
                result.Append(text, start, Math.Min(i, text.Length - 1) - start + 1);
                continue;
            }
            if (c == '/' && i + 1 < text.Length && text[i + 1] == '/')
            {
                while (i < text.Length && text[i] != '\n') i++;
                result.Append('\n');
                continue;
            }
            if (c == '/' && i + 1 < text.Length && text[i + 1] == '*')
            {
                int end = text.IndexOf("*/", i + 2, StringComparison.Ordinal);
                i = end < 0 ? text.Length : end + 1;
                result.Append(' ');
                continue;
            }
            if (c is '}' or ']' && pendingComma >= 0) result[pendingComma] = ' ';
            if (c == ',') pendingComma = result.Length;
            else if (!char.IsWhiteSpace(c)) pendingComma = -1;
            result.Append(c);
        }
        return result.ToString();
    }
}
