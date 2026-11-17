using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace RunicArtifex.DevKit.VisualStudio.Core;

public enum DoctorStatus { Fail, Warn, Pass, Unknown }

public sealed class DoctorCheck
{
    internal DoctorCheck(string id, DoctorStatus status, string message, string? remediation)
    {
        Id = id; Status = status; Message = message; Remediation = remediation;
    }

    public string Id { get; }
    public DoctorStatus Status { get; }
    public string Message { get; }
    public string? Remediation { get; }
}

/// <summary><c>dotnet runic doctor --format json</c> (<c>runic.application.tool.doctor/1</c>, tools/dotnet-runic/README.md "Doctor JSON output").</summary>
public sealed class DoctorReport
{
    internal DoctorReport(string project, bool healthy, int passed, int warnings, int failed, IReadOnlyList<DoctorCheck> checks)
    {
        Project = project; Healthy = healthy; Passed = passed; Warnings = warnings; Failed = failed; Checks = checks;
    }

    public string Project { get; }
    public bool Healthy { get; }
    public int Passed { get; }
    public int Warnings { get; }
    public int Failed { get; }
    /// <summary>In the tool's order.</summary>
    public IReadOnlyList<DoctorCheck> Checks { get; }
}

/// <summary>Severity of an Error List entry.</summary>
public enum RunicErrorSeverity { Error, Warning, Message }

/// <summary>One Error List entry: what VS Code puts in its Problems panel for the same finding.</summary>
public sealed class RunicErrorEntry
{
    internal RunicErrorEntry(RunicErrorSeverity severity, string code, string text, string file, int line, int column, string helpUri)
    {
        Severity = severity; Code = code; Text = text; File = file; Line = line; Column = column; HelpUri = helpUri;
    }

    public RunicErrorSeverity Severity { get; }
    public string Code { get; }
    /// <summary>The Error List description: the code first (the Error List of a task provider has no code column), then the message and remediation.</summary>
    public string Text { get; }
    public string File { get; }
    /// <summary>0-based, as the Error List stores it.</summary>
    public int Line { get; }
    public int Column { get; }
    public string HelpUri { get; }
}

/// <summary>Running and reading <c>dotnet runic doctor</c> as VS Code's <c>vscode/src/doctor.ts</c> and <c>sdk.ts</c> do.</summary>
public static class Doctor
{
    public const string PayloadType = "runic.application.tool.doctor/1";
    /// <summary>VS Code's diagnostic source for doctor problems.</summary>
    public const string Source = "runic doctor";

    /// <summary><c>--fail-on never</c> reports every completed inspection as a successful envelope, so the checks are always in <c>payload</c>.</summary>
    public static IReadOnlyList<string> Arguments(string project)
        => new[] { "runic", "doctor", "--project", project, "--format", "json", "--fail-on", "never" };

    /// <summary>
    /// The doctor report of a <c>runic.commandline/1</c> envelope: its payload, or the payload of a failed envelope's
    /// <c>fault.data</c> (<c>--fail-on fail|warn</c>). A fault without a report (a missing project, an unrestored tool)
    /// throws its code and message.
    /// </summary>
    public static DoctorReport Parse(string stdout)
    {
        Dictionary<string, object?>? envelope;
        try { envelope = Json.Object(Json.Parse(stdout)); }
        catch (FormatException) { envelope = null; }
        if (envelope == null)
        {
            string last = stdout.Trim().Split('\n').LastOrDefault()?.Trim() ?? "";
            throw new InvalidOperationException("dotnet runic doctor did not write a JSON envelope: " + last);
        }
        var fault = Json.Object(Json.Get(envelope, "fault"));
        var data = Json.Object(Json.Get(fault, "data"));
        var report = Json.String(envelope, "payloadType") == PayloadType ? Json.Object(Json.Get(envelope, "payload"))
            : Json.String(data, "type") == PayloadType ? Json.Object(Json.Get(data, "payload")) : null;
        if (report != null && Json.Array(Json.Get(report, "checks")) is { } checks)
        {
            var summary = Json.Object(Json.Get(report, "summary"));
            return new DoctorReport(Json.String(report, "project") ?? "", Json.Boolean(report, "healthy") ?? false,
                (int)(Json.Number(summary, "passed") ?? 0), (int)(Json.Number(summary, "warnings") ?? 0), (int)(Json.Number(summary, "failed") ?? 0),
                checks.Select(Json.Object).Where(check => check != null).Select(check => new DoctorCheck(
                    Json.String(check, "id") ?? "", ParseStatus(Json.String(check, "status")), Json.String(check, "message") ?? "", Json.String(check, "remediation"))).ToList());
        }
        if (fault != null)
        {
            string? code = Json.String(fault, "code");
            throw new InvalidOperationException((code != null ? code + ": " : "") + (Json.String(fault, "message") ?? "dotnet runic doctor failed."));
        }
        throw new InvalidOperationException("dotnet runic doctor returned no report.");
    }

    private static DoctorStatus ParseStatus(string? status) => status switch
    {
        "fail" => DoctorStatus.Fail,
        "warn" => DoctorStatus.Warn,
        "pass" => DoctorStatus.Pass,
        _ => DoctorStatus.Unknown,
    };

    /// <summary>Checks in the order the Doctor section lists them: failures, then warnings, then passing checks, each in the tool's order.</summary>
    public static IReadOnlyList<DoctorCheck> OrderedChecks(DoctorReport report)
        => report.Checks.Select((check, index) => (check, index)).OrderBy(entry => (int)entry.check.Status).ThenBy(entry => entry.index).Select(entry => entry.check).ToList();

    public static string Summary(DoctorReport report)
        => $"{report.Failed} failed, {report.Warnings} warning{(report.Warnings == 1 ? "" : "s")}, {report.Passed} passed";

    /// <summary>
    /// Failing and warning checks as Error List entries on the project file, with the remediation in the text and the
    /// doctor section of the tool's README as the help link, as VS Code's Problems do.
    /// </summary>
    public static IReadOnlyList<RunicErrorEntry> ErrorListEntries(string project, DoctorReport report, string? toolVersion)
    {
        string help = RunicHelpLinks.Doctor(toolVersion);
        return report.Checks.Where(check => check.Status is DoctorStatus.Fail or DoctorStatus.Warn).Select(check => new RunicErrorEntry(
            check.Status == DoctorStatus.Fail ? RunicErrorSeverity.Error : RunicErrorSeverity.Warning, check.Id,
            $"{check.Id}: {check.Message}{(check.Remediation != null ? " Fix: " + check.Remediation : "")}", project, 0, 0, help)).ToList();
    }

    /// <summary>
    /// Runs <c>&lt;dotnet&gt; runic doctor …</c> in the project's directory and returns its standard output. A non-zero
    /// exit with output still returns it (a failed envelope carries the fault); without output it throws with the last
    /// line of standard error.
    /// </summary>
    public static async Task<string> RunAsync(string dotnet, string project, CancellationToken cancellationToken)
    {
        var arguments = Arguments(project);
        var start = new ProcessStartInfo
        {
            FileName = dotnet,
            Arguments = string.Join(" ", arguments.Select(CommandLine.Quote)),
            WorkingDirectory = Path.GetDirectoryName(project) ?? Environment.CurrentDirectory,
            UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8,
        };
        start.EnvironmentVariables["DOTNET_NOLOGO"] = "1";
        using var process = new Process { StartInfo = start, EnableRaisingEvents = true };
        var exited = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        process.Exited += (_, _) => exited.TrySetResult(true);
        try { if (!process.Start()) throw new InvalidOperationException($"Could not start {dotnet}."); }
        catch (System.ComponentModel.Win32Exception error) { throw new InvalidOperationException($"Could not start {dotnet}: {error.Message}", error); }
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        using (cancellationToken.Register(() => { try { process.Kill(); } catch (InvalidOperationException) { } }))
        {
            await Task.WhenAll(stdout, stderr, exited.Task).ConfigureAwait(false);
        }
        cancellationToken.ThrowIfCancellationRequested();
        process.WaitForExit();
        string output = stdout.Result;
        if (process.ExitCode != 0 && output.Trim().Length == 0)
        {
            string detail = (stderr.Result.Trim().Length > 0 ? stderr.Result : $"exit code {process.ExitCode}").Trim().Split('\n').Last().Trim();
            throw new InvalidOperationException($"{Path.GetFileName(dotnet)} runic doctor failed: {detail}");
        }
        return output;
    }
}

/// <summary>Command-line quoting for <see cref="ProcessStartInfo.Arguments"/> (no ArgumentList on .NET Framework).</summary>
public static class CommandLine
{
    /// <summary>Windows <c>CommandLineToArgvW</c> rules, which .NET also applies when it splits Arguments on Unix.</summary>
    public static string Quote(string value)
    {
        if (value.Length > 0 && value.All(character => !char.IsWhiteSpace(character) && character != '"')) return value;
        var result = new StringBuilder("\"");
        int slashes = 0;
        foreach (char character in value)
        {
            if (character == '\\') { slashes++; continue; }
            if (character == '"') result.Append('\\', slashes * 2 + 1).Append('"');
            else result.Append('\\', slashes).Append(character);
            slashes = 0;
        }
        return result.Append('\\', slashes * 2).Append('"').ToString();
    }
}
