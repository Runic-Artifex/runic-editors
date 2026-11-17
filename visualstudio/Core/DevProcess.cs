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
/// <see cref="StopAsync(TimeSpan)"/> ends it with its Window, frontend server and build processes. With
/// <see cref="StopOnInput"/> (<c>dotnet-runic</c> 0.22, WIN-06) it first writes <c>stop</c> to <c>dev</c>'s standard input
/// and closes it, so <c>dev</c> runs its own stop (the session file says <c>"stopped"</c>, the frontend marker and the
/// development pages go). When that is off or <c>dev</c> has not exited in time: on Windows it ends the process tree
/// (no console to send Ctrl+C to), elsewhere it sends SIGINT, as Ctrl+C would.
/// </summary>
public sealed class DevProcess : IDisposable
{
    private readonly Process process;
    private readonly object lineGate = new();
    private readonly Action<string> onLine;
    private readonly TaskCompletionSource<int> exited = new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>The variable that asks <c>dotnet runic dev</c> (0.22) to stop at a <c>stop</c> line or the end of its standard input.</summary>
    public const string StopOnInputVariable = "RUNIC_DEV_STOP_ON_INPUT";

    /// <summary>How long <see cref="StopAsync(TimeSpan)"/> waits for <c>dev</c> to stop on its input before it ends the process.</summary>
    public static readonly TimeSpan DefaultInputStopTimeout = TimeSpan.FromSeconds(10);

    private DevProcess(Process process, string project, bool noWindow, bool stopOnInput, Action<string> onLine)
    {
        this.process = process; Project = project; NoWindow = noWindow; StopOnInput = stopOnInput; this.onLine = onLine;
    }

    /// <summary>Whether <c>dev</c> was started with <see cref="StopOnInputVariable"/> and is first asked to stop on its input.</summary>
    public bool StopOnInput { get; }

    public string Project { get; }
    public bool NoWindow { get; }
    public int ProcessId { get; private set; }
    /// <summary>The exit code, when the process has exited and its output is read.</summary>
    public Task<int> Exited => exited.Task;
    public bool HasExited => exited.Task.IsCompleted;
    /// <summary>The command line, for the Output pane.</summary>
    public string CommandLine { get; private set; } = "";

    /// <summary>Starts <c>&lt;dotnet&gt; runic dev [--no-window | --debug-endpoint] --project &lt;project&gt;</c>.</summary>
    /// <c>stopOnInput</c> (for <c>dotnet-runic</c> 0.22 and later) sets <see cref="StopOnInputVariable"/>.
    public static DevProcess Start(string dotnet, string project, bool noWindow, Action<string> onLine, bool debugEndpoint = false, bool stopOnInput = false)
        => Start(dotnet, DevOutput.Arguments(project, noWindow, debugEndpoint).ToArray(), project, noWindow, onLine, stopOnInput);

    /// <summary>Starts another program in the project's directory (tests use a stand-in for <c>dotnet</c>).</summary>
    public static DevProcess Start(string program, string[] arguments, string project, bool noWindow, Action<string> onLine, bool stopOnInput = false)
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
        if (stopOnInput) start.EnvironmentVariables[StopOnInputVariable] = "1";
        else start.EnvironmentVariables.Remove(StopOnInputVariable);
        var process = new Process { StartInfo = start, EnableRaisingEvents = true };
        var dev = new DevProcess(process, project, noWindow, stopOnInput, onLine) { CommandLine = program + " " + start.Arguments };
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

    /// <summary>
    /// Ends the process and its children; returns how, once it has exited, or after <paramref name="timeout"/> past the
    /// stop request on its input.
    /// </summary>
    public Task<DevStop> StopAsync(TimeSpan timeout) => StopAsync(DefaultInputStopTimeout, timeout);

    /// <summary>
    /// With <see cref="StopOnInput"/>, writes <c>stop</c> and closes the input, then waits up to
    /// <paramref name="inputTimeout"/>; else, or after that, ends it by force as before and waits up to <paramref name="timeout"/>.
    /// The request is written before the first await, so a caller that cannot wait (Visual Studio closing) still sends it.
    /// </summary>
    public async Task<DevStop> StopAsync(TimeSpan inputTimeout, TimeSpan timeout)
    {
        if (HasExited) return DevStop.AlreadyExited;
        if (StopOnInput && RequestStop())
        {
            if (await Task.WhenAny(Exited, Task.Delay(inputTimeout)).ConfigureAwait(false) == Exited) return DevStop.Input;
        }
        if (HasExited) return StopOnInput ? DevStop.Input : DevStop.AlreadyExited;
        try
        {
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows)) Run("taskkill", $"/PID {ProcessId} /T /F");
            else Run("kill", $"-INT {ProcessId}");
        }
        catch (Exception error) when (error is InvalidOperationException or System.ComponentModel.Win32Exception) { }
        if (await Task.WhenAny(Exited, Task.Delay(timeout)).ConfigureAwait(false) == Exited) return DevStop.Forced;
        try { process.Kill(); } catch (InvalidOperationException) { } catch (System.ComponentModel.Win32Exception) { }
        await Task.WhenAny(Exited, Task.Delay(TimeSpan.FromSeconds(5))).ConfigureAwait(false);
        return DevStop.Forced;
    }

    /// <summary>Writes the <c>stop</c> line and closes the input; false when the pipe is gone already.</summary>
    private bool RequestStop()
    {
        try
        {
            var input = process.StandardInput;
            input.WriteLine("stop");
            input.Flush();
            input.Close();
            return true;
        }
        catch (Exception error) when (error is IOException or InvalidOperationException or ObjectDisposedException) { return false; }
    }

    private static void Run(string program, string arguments)
    {
        using var helper = Process.Start(new ProcessStartInfo(program, arguments) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true });
        helper?.WaitForExit(10000);
    }

    public void Dispose() => process.Dispose();
}

/// <summary>How <see cref="DevProcess.StopAsync(TimeSpan, TimeSpan)"/> ended <c>dev</c>.</summary>
public enum DevStop
{
    /// <summary>It had exited before the stop.</summary>
    AlreadyExited,
    /// <summary>It stopped on the <c>stop</c> request on its standard input, running its own stop.</summary>
    Input,
    /// <summary>It was ended by force (the process tree on Windows, SIGINT and then a kill elsewhere).</summary>
    Forced,
}
