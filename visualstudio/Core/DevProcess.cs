using System;
using System.Diagnostics;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace RunicArtifex.DevKit.VisualStudio.Core;

/// <summary>
/// A <c>dotnet runic dev</c> that the extension started, as VS Code's <c>dev</c> task: a child process in the project's
/// directory whose standard output and error lines reach <c>onLine</c> one at a time, in order per stream.
/// <see cref="StopAsync(TimeSpan)"/> ends it with its Window, frontend server and build processes. With
/// <see cref="StopOnInput"/> (<c>dotnet-runic</c> 0.22, WIN-06) it first writes <c>stop</c> to <c>dev</c>'s standard input
/// and closes it, so <c>dev</c> runs its own stop (the session file says <c>"stopped"</c>, the frontend marker and the
/// development pages go). When that is off or <c>dev</c> has not exited in time: on Windows it ends the process tree
/// (no console to send Ctrl+C to), elsewhere it sends SIGINT to the launcher and every process under it, as Ctrl+C in a
/// terminal would (<see cref="PosixProcessTree"/>, runic-editors#53).
/// <para>
/// <c>dev</c> has exited when its process has, not when its output pipes close: a frontend server it leaves behind
/// holds them open (the 0.22 checklist, row 6). On Windows <c>dev</c> runs in a Job object (<see cref="DevProcessTree"/>),
/// so after a stop the processes it left behind are ended when it exits, and a forced stop ends all of them, also those
/// whose parent has exited already (which <c>taskkill /T</c> cannot find).
/// </para>
/// </summary>
public sealed class DevProcess : IDisposable
{
    private readonly Process process;
    private readonly object lineGate = new();
    private readonly Action<string> onLine;
    private readonly TaskCompletionSource<int> exited = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource<bool> outputClosed = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource<bool> errorClosed = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private DevProcessTree? tree;
    private int exitHandled;
    private volatile bool stopRequested;

    /// <summary>The variable that asks <c>dotnet runic dev</c> (0.22) to stop at a <c>stop</c> line or the end of its standard input.</summary>
    public const string StopOnInputVariable = "RUNIC_DEV_STOP_ON_INPUT";

    /// <summary>How long <see cref="StopAsync(TimeSpan)"/> waits for <c>dev</c> to stop on its input before it ends the process.</summary>
    public static readonly TimeSpan DefaultInputStopTimeout = TimeSpan.FromSeconds(10);

    /// <summary>How long the output's last lines are read after <c>dev</c> exits; processes it left behind can keep the pipes open.</summary>
    public static readonly TimeSpan OutputDrainTimeout = TimeSpan.FromSeconds(2);

    private DevProcess(Process process, string project, bool noWindow, bool stopOnInput, Action<string> onLine)
    {
        this.process = process; Project = project; NoWindow = noWindow; StopOnInput = stopOnInput; this.onLine = onLine;
    }

    /// <summary>Whether <c>dev</c> was started with <see cref="StopOnInputVariable"/> and is first asked to stop on its input.</summary>
    public bool StopOnInput { get; }

    public string Project { get; }
    public bool NoWindow { get; }
    public int ProcessId { get; private set; }
    /// <summary>
    /// The exit code, once the process has exited and its output is read, or <see cref="OutputDrainTimeout"/> after the
    /// exit when processes it left behind hold the output open.
    /// </summary>
    public Task<int> Exited => exited.Task;
    public bool HasExited => exited.Task.IsCompleted;
    /// <summary>The command line, for the Output pane.</summary>
    public string CommandLine { get; private set; } = "";
    /// <summary>
    /// How many processes <c>dev</c> had left running when it exited after <see cref="StopAsync(TimeSpan, TimeSpan)"/>,
    /// which were then ended (its frontend server, <c>runic-translations serve</c>); 0 elsewhere than on Windows, and when
    /// <c>dev</c> exited on its own (nobody asked to stop what it started). Read it once <see cref="Exited"/> completes.
    /// </summary>
    public int EndedLeftovers { get; private set; }

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
        process.OutputDataReceived += (_, args) => { if (args.Data == null) dev.outputClosed.TrySetResult(true); else dev.Line(args.Data); };
        process.ErrorDataReceived += (_, args) => { if (args.Data == null) dev.errorClosed.TrySetResult(true); else dev.Line(args.Data); };
        // Not WaitForExit(): without a timeout it waits for the pipes to close, which a process dev left behind delays forever.
        process.Exited += (_, _) => _ = dev.OnExitedAsync();
        try { if (!process.Start()) throw new InvalidOperationException($"Could not start {program}."); }
        catch (System.ComponentModel.Win32Exception error) { process.Dispose(); throw new InvalidOperationException($"Could not start {program}: {error.Message}", error); }
        dev.ProcessId = process.Id;
        // The job takes the process now and, as children it started before this line run outside the job unless assigned
        // too (runic-editors#58), what it has started already; everything those start later is in the job by inheritance.
        dev.tree = DevProcessTree.TryAssign(process);
        try { dev.tree?.AssignDescendants(process); }
        catch (Exception error) when (error is InvalidOperationException or System.ComponentModel.Win32Exception or DllNotFoundException or EntryPointNotFoundException) { }
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        // An exit before the handler was attached to the started process raises no Exited event.
        if (process.HasExited) _ = dev.OnExitedAsync();
        return dev;
    }

    /// <summary>Ends what <c>dev</c> left running, reads the output's last lines for a short while, then completes <see cref="Exited"/>.</summary>
    private async Task OnExitedAsync()
    {
        if (System.Threading.Interlocked.Exchange(ref exitHandled, 1) != 0) return;
        int code;
        try { code = process.ExitCode; }
        catch (InvalidOperationException) { code = -1; }
        if (tree != null && stopRequested) EndedLeftovers = tree.EndAll();
        await Task.WhenAny(Task.WhenAll(outputClosed.Task, errorClosed.Task), Task.Delay(OutputDrainTimeout)).ConfigureAwait(false);
        exited.TrySetResult(code);
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
    /// Cancelling <paramref name="cancellationToken"/> (Visual Studio closing, runic-editors#44) stops the waiting: it throws
    /// <see cref="OperationCanceledException"/> before the next step, and the stop already requested goes on without it.
    /// </summary>
    public async Task<DevStop> StopAsync(TimeSpan inputTimeout, TimeSpan timeout, CancellationToken cancellationToken = default)
    {
        if (HasExited) return DevStop.AlreadyExited;
        stopRequested = true;
        if (StopOnInput && RequestStop())
        {
            if (await Task.WhenAny(Exited, Task.Delay(inputTimeout, cancellationToken)).ConfigureAwait(false) == Exited) return DevStop.Input;
            cancellationToken.ThrowIfCancellationRequested();
        }
        if (HasExited) return StopOnInput ? DevStop.Input : DevStop.AlreadyExited;
        bool windows = RuntimeInformation.IsOSPlatform(OSPlatform.Windows);
        // Off Windows: the launcher and dev under it (runic-editors#53), signalled together as Ctrl+C signals a terminal's group.
        IReadOnlyList<int> posixTree = windows ? Array.Empty<int>() : PosixProcessTree.Of(ProcessId);
        try
        {
            if (windows)
            {
                // taskkill first, while dev still runs: it follows parent IDs from dev, which ending the job's processes
                // first would cut (runic-editors#58), and it covers a dev whose job could not be created. Then the job
                // ends the rest, also the processes whose parent has exited.
                Run("taskkill", $"/PID {ProcessId} /T /F");
                tree?.EndAll();
            }
            else PosixProcessTree.Signal("INT", posixTree);
        }
        catch (Exception error) when (error is InvalidOperationException or System.ComponentModel.Win32Exception) { }
        var forceStarted = Stopwatch.StartNew();
        bool exited = await Task.WhenAny(Exited, Task.Delay(timeout, cancellationToken)).ConfigureAwait(false) == Exited;
        cancellationToken.ThrowIfCancellationRequested();
        if (exited)
        {
            // A launcher that ended at the signal can leave dev stopping, or ignoring it: kill what is left after the timeout.
            if (posixTree.Count > 1)
            {
                var left = await PosixProcessTree.WaitForExitAsync(posixTree.Skip(1).ToList(), timeout - forceStarted.Elapsed, cancellationToken).ConfigureAwait(false);
                PosixProcessTree.Signal("KILL", left);
            }
            return DevStop.Forced;
        }
        tree?.EndAll();
        PosixProcessTree.Signal("KILL", posixTree.Skip(1));
        try { process.Kill(); } catch (InvalidOperationException) { } catch (System.ComponentModel.Win32Exception) { }
        await Task.WhenAny(Exited, Task.Delay(TimeSpan.FromSeconds(5))).ConfigureAwait(false);
        return DevStop.Forced;
    }

    /// <summary>Writes a line to the process's input (tests: a stand-in that waits for it).</summary>
    internal void WriteInputLine(string line)
    {
        process.StandardInput.WriteLine(line);
        process.StandardInput.Flush();
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

    public void Dispose()
    {
        process.Dispose();
        tree?.Dispose();
    }
}

/// <summary>How <see cref="DevProcess.StopAsync(TimeSpan, TimeSpan)"/> ended <c>dev</c>.</summary>
public enum DevStop
{
    /// <summary>It had exited before the stop.</summary>
    AlreadyExited,
    /// <summary>It stopped on the <c>stop</c> request on its standard input, running its own stop.</summary>
    Input,
    /// <summary>It was ended by force (its Job object and process tree on Windows, SIGINT and then a kill elsewhere).</summary>
    Forced,
}
