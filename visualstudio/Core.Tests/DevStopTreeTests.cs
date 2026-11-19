using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using TUnit.Core;

namespace RunicArtifex.DevKit.VisualStudio.Core.Tests;

/// <summary>
/// runic-editors#53: the forced stop reaches <c>dotnet runic dev</c> behind its <c>dotnet</c> launcher, which forwards no
/// signal: off Windows through the process tree, on Windows through the Job object (editors#41).
/// </summary>
public class DevStopTreeTests
{
    [Test]
    public void The_tree_is_read_from_ps_output()
    {
        var parents = PosixProcessTree.ParseParents("    1     0\n  100     1\n  101   100\n  102   101\n  103   101\n  200     1\n  garbage\n  104   103\r\n");
        Check.Same(new[] { 100, 101, 102, 103, 104 }, PosixProcessTree.Tree(100, parents), "launcher, dev, dev's children");
        Check.Same(new[] { 200 }, PosixProcessTree.Tree(200, parents), "no children");
        Check.Same(new[] { 999 }, PosixProcessTree.Tree(999, parents), "an exited root is itself");
    }

    /// <summary>A launcher stand-in that runs <c>dev</c> as its child and forwards no signal; <c>dev</c> prints its PID.</summary>
    private static (DevProcess Launcher, Func<Task<int>> DevPid) LauncherWithDev(string root, string devScript, List<string> lines)
    {
        var launcher = DevProcess.Start("/bin/sh", new[] { "-c", $"echo '[dev] Ready'; sh -c '{devScript}'; echo launcher-done" }, Path.Combine(root, "App.csproj"), noWindow: true, line => { lock (lines) lines.Add(line); }, stopOnInput: true);
        async Task<int> DevPid()
        {
            for (int attempt = 0; attempt < 200; attempt++)
            {
                lock (lines) if (lines.Select(line => Regex.Match(line, @"^dev=(\d+)$")).FirstOrDefault(match => match.Success) is { } found) return int.Parse(found.Groups[1].Value);
                await Task.Delay(50);
            }
            throw new TimeoutException("dev printed no PID");
        }
        return (launcher, DevPid);
    }

    private static async Task<bool> GoneAsync(int processId, TimeSpan within)
    {
        var deadline = DateTime.UtcNow + within;
        while (DateTime.UtcNow < deadline) { if (!PosixProcessTree.IsRunning(processId)) return true; await Task.Delay(50); }
        return !PosixProcessTree.IsRunning(processId);
    }

    private static async Task WithRoot(Func<string, Task> test)
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows)) return;
        string root = Path.Combine(Path.GetTempPath(), $"runic-vs-devtree-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try { await test(root); }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Test]
    public Task A_dev_behind_its_launcher_gets_the_SIGINT() => WithRoot(async root =>
    {
        var lines = new List<string>();
        // dev ignores its input (older or hung) and stops on SIGINT, as dotnet runic dev does on Ctrl+C.
        var (launcher, devPid) = LauncherWithDev(root, "trap \"exit 130\" INT; echo dev=$$; while :; do sleep 0.2; done", lines);
        using (launcher)
        {
            int dev = await devPid();
            Check.Equal(DevStop.Forced, await launcher.StopAsync(TimeSpan.FromMilliseconds(300), TimeSpan.FromSeconds(5)));
            Check.True(await GoneAsync(dev, TimeSpan.FromSeconds(3)), "dev stopped on the SIGINT");
        }
    });

    [Test]
    public Task A_dev_that_ignores_SIGINT_is_killed_with_the_tree() => WithRoot(async root =>
    {
        var lines = new List<string>();
        var (launcher, devPid) = LauncherWithDev(root, "trap \"\" INT; echo dev=$$; while :; do sleep 0.2; done", lines);
        using (launcher)
        {
            int dev = await devPid();
            Check.Equal(DevStop.Forced, await launcher.StopAsync(TimeSpan.FromMilliseconds(300), TimeSpan.FromSeconds(1)));
            Check.True(await GoneAsync(dev, TimeSpan.FromSeconds(4)), "dev killed after the timeout");
        }
    });

    [Test]
    public async Task On_Windows_the_job_ends_dev_behind_its_launcher()
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows)) return;
        string root = Path.Combine(Path.GetTempPath(), $"runic-vs-devjob-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        var lines = new List<string>();
        int dev = 0;
        try
        {
            // The launcher starts dev as a separate process and waits; neither reads its input, as a hung dev.
            const string script = "$d = Start-Process powershell -ArgumentList '-NoProfile','-Command','Start-Sleep 120' -PassThru -WindowStyle Hidden; Write-Output '[dev] Ready'; Write-Output ('dev=' + $d.Id); Start-Sleep 120";
            using var launcher = DevProcess.Start("powershell", new[] { "-NoProfile", "-NonInteractive", "-Command", script }, Path.Combine(root, "App.csproj"), noWindow: true, line => { lock (lines) lines.Add(line); }, stopOnInput: true);
            for (int attempt = 0; attempt < 400 && dev == 0; attempt++)
            {
                lock (lines) if (lines.Select(line => Regex.Match(line, @"^dev=(\d+)$")).FirstOrDefault(match => match.Success) is { } found) dev = int.Parse(found.Groups[1].Value);
                if (dev == 0) await Task.Delay(50);
            }
            Check.True(dev != 0, "dev printed its PID");
            Check.True(PosixProcessTree.IsRunning(dev), "dev runs before the stop");
            Check.Equal(DevStop.Forced, await launcher.StopAsync(TimeSpan.FromMilliseconds(500), TimeSpan.FromSeconds(10)));
            Check.True(await GoneAsync(dev, TimeSpan.FromSeconds(5)), "the job ended dev with its launcher");
        }
        finally
        {
            if (dev != 0 && PosixProcessTree.IsRunning(dev)) try { System.Diagnostics.Process.GetProcessById(dev).Kill(); } catch (Exception) { }
            Directory.Delete(root, recursive: true);
        }
    }
}
