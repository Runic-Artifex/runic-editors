using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace RunicArtifex.DevKit.VisualStudio.Core;

/// <summary>How to run the workspace's local <c>runic-translations</c> tool (the language server's) with other arguments.</summary>
public sealed class TranslationsToolLaunch
{
    internal TranslationsToolLaunch(string dotnet, IReadOnlyList<string> arguments, string directory)
    {
        Dotnet = dotnet; Arguments = arguments; Directory = directory;
    }

    public string Dotnet { get; }
    /// <summary>The arguments of <see cref="Dotnet"/>, unquoted.</summary>
    public IReadOnlyList<string> Arguments { get; }
    /// <summary>The working directory: the tool manifest's, or the start directory for a configured assembly.</summary>
    public string Directory { get; }
    /// <summary><see cref="Arguments"/> quoted for <see cref="ProcessStartInfo.Arguments"/>.</summary>
    public string CommandLineArguments => string.Join(" ", Arguments.Select(CommandLine.Quote));
}

/// <summary>What a <c>runic-translations</c> run printed.</summary>
public sealed class TranslationsToolResult
{
    internal TranslationsToolResult(int exitCode, string stdout, string stderr)
    {
        ExitCode = exitCode; Stdout = stdout; Stderr = stderr;
    }

    public int ExitCode { get; }
    public string Stdout { get; }
    public string Stderr { get; }
}

/// <summary>
/// The Translations commands that run the workspace's local <c>runic-translations</c> tool, as VS Code's
/// <c>vscode/src/server.ts</c> (<c>toolLaunch</c> and the argument builders) and <c>extension.ts</c> (the commands'
/// output handling) do: XLIFF export and import dry run, catalog import, status and <c>overrides add</c>.
/// </summary>
public static class TranslationsTool
{
    public const string ToolMissing = "No project-local runic-translations tool was found. Add it to .config/dotnet-tools.json and run dotnet tool restore, or set " + RunicSettings.ServerAssemblyOption + " for SDK development.";

    /// <summary>
    /// The local tool for <paramref name="toolArguments"/>: a configured server assembly (<c>dotnet &lt;dll&gt; …</c>),
    /// else <c>dotnet tool run runic-translations -- …</c> in the nearest <c>.config/dotnet-tools.json</c> at or above
    /// <paramref name="directory"/> that declares the command, stopping at a manifest with <c>isRoot</c>.
    /// </summary>
    public static TranslationsToolLaunch Find(string directory, string dotnet, ServerAssemblySetting? assembly, IReadOnlyList<string> toolArguments)
    {
        if (assembly != null)
        {
            if (!Path.IsPathRooted(assembly.Path) || !File.Exists(assembly.Path))
                throw new InvalidOperationException($"{assembly.Source} must identify an existing absolute tool DLL; {assembly.Path} is not one.");
            return new TranslationsToolLaunch(dotnet, new[] { assembly.Path }.Concat(toolArguments).ToList(), directory);
        }
        for (string? current = directory; !string.IsNullOrEmpty(current); current = Path.GetDirectoryName(current))
        {
            string manifest = Path.Combine(current, ".config", "dotnet-tools.json");
            if (!File.Exists(manifest)) continue;
            Dictionary<string, object?>? json;
            try { json = Json.Object(Json.Parse(File.ReadAllText(manifest))); }
            catch (FormatException error) { throw new InvalidOperationException($"{manifest} is not valid JSON: {error.Message}"); }
            var tools = Json.Object(Json.Get(json, "tools"));
            if (tools != null && tools.Values.Any(tool => Json.Array(Json.Get(Json.Object(tool), "commands"))?.Contains("runic-translations") == true))
                return new TranslationsToolLaunch(dotnet, new[] { "tool", "run", "runic-translations", "--" }.Concat(toolArguments).ToList(), current!);
            if (Json.Boolean(json, "isRoot") == true) break;
        }
        throw new InvalidOperationException(ToolMissing);
    }

    /// <summary><c>runic-translations xliff export</c> of the project into <paramref name="output"/>: one XLIFF 2.1 document per translated locale.</summary>
    public static IReadOnlyList<string> XliffExportArguments(string project, string output)
        => new[] { "xliff", "export", "--project", project, "--output", output };

    /// <summary><c>runic-translations xliff import --dry-run</c>: the per-unit outcome of importing <paramref name="source"/>, without writing files.</summary>
    public static IReadOnlyList<string> XliffImportDryRunArguments(string project, string source)
        => new[] { "xliff", "import", "--project", project, "--source", source, "--dry-run" };

    /// <summary><c>runic-translations status --format json</c> for one project: coverage per locale.</summary>
    public static IReadOnlyList<string> StatusArguments(string project)
        => new[] { "status", "--project", project, "--format", "json" };

    /// <summary><c>runic-translations import --from resx|i18next &lt;source&gt;</c>, first with <c>--dry-run</c>; <c>--overwrite</c> replaces existing text.</summary>
    public static IReadOnlyList<string> ImportArguments(string project, CatalogFormat from, string source, bool dryRun, bool overwrite = false)
    {
        var arguments = new List<string> { "import", "--from", from == CatalogFormat.Resx ? "resx" : "i18next", source, "--project", project };
        if (overwrite) arguments.Add("--overwrite");
        if (dryRun) arguments.Add("--dry-run");
        return arguments;
    }

    /// <summary><c>runic-translations overrides add &lt;catalog&gt; --locale &lt;tag&gt; [--key &lt;key&gt;]</c> for the project of <paramref name="manifest"/> (its runic.json).</summary>
    public static IReadOnlyList<string> OverridesAddArguments(string manifest, string catalog, string locale, string? key = null)
    {
        var arguments = new List<string> { "overrides", "add", catalog, "--locale", locale };
        if (!string.IsNullOrEmpty(key)) { arguments.Add("--key"); arguments.Add(key!); }
        arguments.Add("--project"); arguments.Add(Path.GetDirectoryName(manifest) ?? manifest);
        return arguments;
    }

    /// <summary>The project manifest of a folder: <c>runic.json</c>, else <c>translations/runic.json</c>.</summary>
    public static string? ProjectManifest(string root)
        => new[] { Path.Combine(root, "runic.json"), Path.Combine(root, "translations", "runic.json") }.FirstOrDefault(File.Exists);

    /// <summary>
    /// The runic.json of the project a file belongs to: the nearest one in its directory or above, up to and including
    /// <paramref name="root"/> (the solution directory), as the language server resolves it; null when none is within it.
    /// Without a root, the search goes up to the file system root.
    /// </summary>
    public static string? NearestProjectManifest(string file, string? root)
    {
        string? boundary = string.IsNullOrEmpty(root) ? null : Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        string? directory = Path.GetDirectoryName(Path.GetFullPath(file));
        if (boundary != null && directory != null && !IsWithin(boundary, directory)) return null;
        for (; !string.IsNullOrEmpty(directory); directory = Path.GetDirectoryName(directory))
        {
            string candidate = Path.Combine(directory!, "runic.json");
            if (File.Exists(candidate)) return candidate;
            if (boundary != null && string.Equals(directory!.TrimEnd(Path.DirectorySeparatorChar), boundary, PathComparison)) return null;
        }
        return null;
    }

    /// <summary>The number of files the tool wrote (its <c>wrote …</c> lines).</summary>
    public static int WrittenFiles(string stdout) => Lines(stdout).Count(line => line.StartsWith("wrote ", StringComparison.Ordinal));

    /// <summary>The <c>dry run: …</c> summary line of an import, or a generic text.</summary>
    public static string DryRunSummary(string stdout) => Lines(stdout).FirstOrDefault(line => line.StartsWith("dry run: ", StringComparison.Ordinal)) ?? "Dry run finished.";

    /// <summary>True when an import's dry run kept existing text that <c>--overwrite</c> would replace.</summary>
    public static bool KeptExisting(string stdout) => Lines(stdout).Any(line => line.Contains("were kept; --overwrite replaces them"));

    /// <summary>The message after Export XLIFF.</summary>
    public static string ExportedMessage(string stdout, string target) => $"Exported {WrittenFiles(stdout)} XLIFF document(s) to {target}.";

    /// <summary>The message after Import XLIFF (Dry Run).</summary>
    public static string XliffDryRunMessage(string stdout)
        => $"{DryRunSummary(stdout)} Nothing was written; run runic-translations xliff import or the Translations Editor to apply it.";

    /// <summary>The question after a catalog import's dry run.</summary>
    public static string ImportDryRunQuestion(string stdout) => $"{DryRunSummary(stdout)} Nothing was written yet; the Runic Translations output lists each message.";

    /// <summary>The message after a catalog import.</summary>
    public static string ImportedMessage(CatalogFormat from, string stdout)
        => $"Imported {(from == CatalogFormat.Resx ? ".resx" : "i18next")}: wrote {WrittenFiles(stdout)} file(s). See the Runic Translations output for each message.";

    /// <summary>
    /// The error of a failed run: <c>runic-translations xliff export failed (&lt;last line&gt;). See the Runic Translations output.</c>
    /// </summary>
    public static string FailureMessage(IReadOnlyList<string> toolArguments, TranslationsToolResult result)
    {
        string text = (result.Stderr.Trim().Length > 0 ? result.Stderr : result.Stdout).Trim();
        string last = text.Length > 0 ? Lines(text).Last() : $"exit code {result.ExitCode}";
        return $"runic-translations {string.Join(" ", toolArguments.Take(2))} failed ({last}). See the Runic Translations output.";
    }

    /// <summary>
    /// Runs the tool and returns its output; <paramref name="writeLine"/> receives the command line and then its output, as
    /// VS Code's <c>runLocalTool</c> writes to the output channel. A non-zero exit throws <see cref="FailureMessage"/>.
    /// </summary>
    public static async Task<TranslationsToolResult> RunAsync(TranslationsToolLaunch launch, IReadOnlyList<string> toolArguments, Action<string>? writeLine, CancellationToken cancellationToken)
    {
        writeLine?.Invoke("> runic-translations " + string.Join(" ", toolArguments.Select(CommandLine.Quote)));
        var start = new ProcessStartInfo
        {
            FileName = launch.Dotnet, Arguments = launch.CommandLineArguments, WorkingDirectory = launch.Directory,
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8,
        };
        start.EnvironmentVariables["DOTNET_NOLOGO"] = "1";
        using var process = new Process { StartInfo = start, EnableRaisingEvents = true };
        var exited = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        process.Exited += (_, _) => exited.TrySetResult(true);
        try { if (!process.Start()) throw new InvalidOperationException($"Could not start {launch.Dotnet}."); }
        catch (System.ComponentModel.Win32Exception error) { throw new InvalidOperationException($"Could not start {launch.Dotnet}: {error.Message}", error); }
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        using (cancellationToken.Register(() => { try { process.Kill(); } catch (InvalidOperationException) { } }))
            await Task.WhenAll(stdout, stderr, exited.Task).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        process.WaitForExit();
        var result = new TranslationsToolResult(process.ExitCode, stdout.Result, stderr.Result);
        if (writeLine != null)
        {
            if (result.Stdout.Trim().Length > 0) writeLine(result.Stdout.TrimEnd());
            if (result.Stderr.Trim().Length > 0) writeLine(result.Stderr.TrimEnd());
        }
        if (result.ExitCode != 0) throw new InvalidOperationException(FailureMessage(toolArguments, result));
        return result;
    }

    internal static IEnumerable<string> Lines(string text) => text.Split('\n').Select(line => line.TrimEnd('\r'));

    internal static StringComparison PathComparison => Path.DirectorySeparatorChar == '\\' ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    internal static bool IsWithin(string root, string path)
    {
        string prefix = root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        string full = path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        return string.Equals(full, prefix, PathComparison) || full.StartsWith(prefix + Path.DirectorySeparatorChar, PathComparison);
    }
}

/// <summary>The formats Import .resx or i18next Catalog reads.</summary>
public enum CatalogFormat { Resx, I18next }
