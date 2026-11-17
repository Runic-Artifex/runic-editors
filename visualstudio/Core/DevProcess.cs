using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;

namespace RunicArtifex.DevKit.VisualStudio.Core;

/// <summary>
/// A <c>dotnet runic dev</c> that the extension started, as VS Code's <c>dev</c> task: a child process in the project's
/// directory whose standard output and error lines reach <c>onLine</c> one at a time, in order per stream.
/// <see cref="StopAsync"/> ends it with its Window, frontend server and build processes: on Windows by ending the process tree,
/// as VS Code terminates a task there (no console to send Ctrl+C to); elsewhere with SIGINT, as Ctrl+C would.
/// </summary>
public sealed class DevProcess : IDisposable
{
    private readonly Process process;
    private readonly object lineGate = new();
    private readonly Action<string> onLine;
    private readonly TaskCompletionSource<int> exited = new(TaskCreationOptions.RunContinuationsAsynchronously);

    private DevProcess(Process process, string project, bool noWindow, Action<string> onLine)
    {
        this.process = process; Project = project; NoWindow = noWindow; this.onLine = onLine;
    }

    public string Project { get; }
    public bool NoWindow { get; }
    public int ProcessId { get; private set; }
    /// <summary>The exit code, when the process has exited and its output is read.</summary>
    public Task<int> Exited => exited.Task;
    public bool HasExited => exited.Task.IsCompleted;
    /// <summary>The command line, for the Output pane.</summary>
    public string CommandLine { get; private set; } = "";

    /// <summary>Starts <c>&lt;dotnet&gt; runic dev [--no-window] --project &lt;project&gt;</c>.</summary>
    public static DevProcess Start(string dotnet, string project, bool noWindow, Action<string> onLine)
        => Start(dotnet, DevOutput.Arguments(project, noWindow).ToArray(), project, noWindow, onLine);

    /// <summary>Starts another program in the project's directory (tests use a stand-in for <c>dotnet</c>).</summary>
    public static DevProcess Start(string program, string[] arguments, string project, bool noWindow, Action<string> onLine)
    {
        var start = new ProcessStartInfo
        {
            FileName = program,
            Arguments = string.Join(" ", arguments.Select(Core.CommandLine.Quote)),
            WorkingDirectory = Path.GetDirectoryName(project) ?? Environment.CurrentDirectory,
            UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8,
        };
        start.EnvironmentVariables["DOTNET_NOLOGO"] = "1";
        var process = new Process { StartInfo = start, EnableRaisingEvents = true };
        var dev = new DevProcess(process, project, noWindow, onLine) { CommandLine = program + " " + start.Arguments };
        process.OutputDataReceived += (_, args) => dev.Line(args.Data);
        process.ErrorDataReceived += (_, args) => dev.Line(args.Data);
        process.Exited += (_, _) =>
        {
            // WaitForExit() without a timeout also waits for the redirected streams' last lines.
            try { process.WaitForExit(); dev.exited.TrySetResult(process.ExitCode); }
            catch (InvalidOperationException) { dev.exited.TrySetResult(-1); }
        };
        try { if (!process.Start()) throw new InvalidOperationException($"Could not start {program}."); }
        catch (System.ComponentModel.Win32Exception error) { process.Dispose(); throw new InvalidOperationException($"Could not start {program}: {error.Message}", error); }
        dev.ProcessId = process.Id;
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        return dev;
    }

    private void Line(string? line)
    {
        if (line == null) return;
        lock (lineGate) onLine(line);
    }

    /// <summary>Ends the process and its children; returns once it has exited, or after <paramref name="timeout"/>.</summary>
    public async Task StopAsync(TimeSpan timeout)
    {
        if (HasExited) return;
        try
        {
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows)) Run("taskkill", $"/PID {ProcessId} /T /F");
            else Run("kill", $"-INT {ProcessId}");
        }
        catch (Exception error) when (error is InvalidOperationException or System.ComponentModel.Win32Exception) { }
        if (await Task.WhenAny(Exited, Task.Delay(timeout)).ConfigureAwait(false) == Exited) return;
        try { process.Kill(); } catch (InvalidOperationException) { } catch (System.ComponentModel.Win32Exception) { }
        await Task.WhenAny(Exited, Task.Delay(TimeSpan.FromSeconds(5))).ConfigureAwait(false);
    }

    private static void Run(string program, string arguments)
    {
        using var helper = Process.Start(new ProcessStartInfo(program, arguments) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true });
        helper?.WaitForExit(10000);
    }

    public void Dispose() => process.Dispose();
}
