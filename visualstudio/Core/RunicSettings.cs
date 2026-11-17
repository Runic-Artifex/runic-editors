using System;
using System.IO;

namespace RunicArtifex.DevKit.VisualStudio.Core;

/// <summary>How much of the communication with the language server the Translations Output pane shows (VS Code's <c>runic.translations.trace.server</c>).</summary>
public enum LspTraceLevel
{
    Off,
    Messages,
    Verbose,
}

/// <summary>
/// Tools → Options → Runic DevKit, the Visual Studio counterparts of VS Code's <c>runic.*</c> settings (PARITY §5).
/// Empty text options fall back to the environment variables the extension read before it had options.
/// </summary>
public static class RunicSettings
{
    public const string OptionsCategory = "Runic DevKit";
    public const string OptionsPage = "General";
    public const string DotnetVariable = "RUNIC_DOTNET";
    public const string ServerAssemblyVariable = "RUNIC_TRANSLATIONS_SERVER";
    public const string DefaultDotnet = "dotnet";

    /// <summary>Where the server assembly option is set, as messages name it.</summary>
    public const string ServerAssemblyOption = "Tools > Options > " + OptionsCategory + " > " + OptionsPage + " > Language server assembly";

    /// <summary><c>runic.dotnetPath</c>: the option, else <c>RUNIC_DOTNET</c>, else <c>dotnet</c> on the PATH.</summary>
    public static string Dotnet(string? option, string? environment)
        => NonEmpty(option) ?? NonEmpty(environment) ?? DefaultDotnet;

    /// <summary>
    /// <c>runic.translations.serverAssembly</c>: the option (absolute, or relative to the solution directory, as VS Code's
    /// is relative to the workspace folder), else <c>RUNIC_TRANSLATIONS_SERVER</c> (absolute). Null runs the local tool.
    /// </summary>
    public static ServerAssemblySetting? ServerAssembly(string? option, string? environment, string? solutionDirectory)
    {
        if (NonEmpty(option) is { } path)
        {
            if (!Path.IsPathRooted(path))
            {
                if (string.IsNullOrEmpty(solutionDirectory))
                    throw new InvalidOperationException($"{ServerAssemblyOption} is relative ({path}), but no solution is open to resolve it against.");
                path = Path.GetFullPath(Path.Combine(solutionDirectory!, path));
            }
            return new ServerAssemblySetting(path, ServerAssemblyOption);
        }
        return NonEmpty(environment) is { } variable ? new ServerAssemblySetting(variable, ServerAssemblyVariable) : null;
    }

    /// <summary>Parses a stored trace level; unknown text is <see cref="LspTraceLevel.Off"/>.</summary>
    public static LspTraceLevel ParseTrace(string? value)
        => Enum.TryParse(value?.Trim(), ignoreCase: true, out LspTraceLevel level) && Enum.IsDefined(typeof(LspTraceLevel), level) ? level : LspTraceLevel.Off;

    private static string? NonEmpty(string? value) => string.IsNullOrWhiteSpace(value) ? null : value!.Trim();
}

/// <summary>A configured language server assembly and the setting it came from.</summary>
public sealed class ServerAssemblySetting
{
    internal ServerAssemblySetting(string path, string source) { Path = path; Source = source; }

    public string Path { get; }
    /// <summary><see cref="RunicSettings.ServerAssemblyOption"/> or <see cref="RunicSettings.ServerAssemblyVariable"/>.</summary>
    public string Source { get; }
}
