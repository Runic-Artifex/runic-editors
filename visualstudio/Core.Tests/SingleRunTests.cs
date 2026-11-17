using System;
using System.Threading;
using System.Threading.Tasks;
using TUnit.Core;

namespace RunicArtifex.DevKit.VisualStudio.Core.Tests;

/// <summary>
/// runic-editors#23: Debug the Window's running state recovers from a hung attach and a solution reload without a
/// Visual Studio restart.
/// </summary>
public class SingleRunTests
{
    [Test]
    public void A_run_holds_the_command_until_it_is_released()
    {
        var run = new SingleRun();
        var ticket = run.TryStart(CancellationToken.None);
        Check.True(ticket != null, "first run");
        Check.True(run.IsRunning, "disabled while it runs");
        Check.True(run.TryStart(CancellationToken.None) == null, "one at a time");
        Check.True(run.Release(ticket!), "released");
        Check.True(!run.IsRunning, "enabled again");
        Check.True(ticket!.Token.IsCancellationRequested, "the released run's token is cancelled");
        Check.True(!run.Release(ticket), "a second release does nothing");
    }

    [Test]
    public void A_solution_reload_resets_a_stuck_run_and_its_late_finish_leaves_the_next_run_alone()
    {
        var run = new SingleRun();
        var stuck = run.TryStart(CancellationToken.None)!;
        // The solution closed and opened again (as after deleting .vs) while Attach2 never returned.
        Check.True(run.Reset(), "a run was waiting");
        Check.True(!run.IsRunning, "the command is enabled again without a restart");
        Check.True(stuck.Token.IsCancellationRequested, "the stuck run stops at its next await");
        Check.True(!stuck.IsCurrent, "the stuck run no longer holds the command");
        Check.True(!run.Reset(), "nothing to reset");

        var next = run.TryStart(CancellationToken.None);
        Check.True(next != null, "Debug the Window runs again");
        // The stuck Attach2 returns at last: its finally must not enable the command while the next run holds it.
        Check.True(!run.Release(stuck), "the stale ticket");
        Check.True(run.IsRunning, "the next run still holds the command");
        Check.True(next!.IsCurrent && !next.Token.IsCancellationRequested, "the next run goes on");
    }

    [Test]
    public void A_run_follows_the_package_token()
    {
        using var package = new CancellationTokenSource();
        var run = new SingleRun();
        var ticket = run.TryStart(package.Token)!;
        package.Cancel();
        Check.True(ticket.Token.IsCancellationRequested, "Visual Studio closing cancels the run");
    }

    [Test]
    public async Task The_watchdog_fires_once_for_a_call_that_does_not_return()
    {
        var fired = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        int count = 0;
        var run = new SingleRun();
        var ticket = run.TryStart(CancellationToken.None)!;
        // As the hung Attach2: the watchdog releases the run (re-enabling the command) while the call still blocks.
        using (var watchdog = Watchdog.Start(TimeSpan.FromMilliseconds(100), () => { Interlocked.Increment(ref count); run.Release(ticket); fired.TrySetResult(true); }))
        {
            Check.True(await Task.WhenAny(fired.Task, Task.Delay(TimeSpan.FromSeconds(10))) == fired.Task, "fired");
            Check.True(watchdog.Expired, "expired");
        }
        Check.Equal(1, count);
        Check.True(!run.IsRunning, "the command is enabled again");
    }

    [Test]
    public async Task The_watchdog_stays_quiet_when_the_call_returns()
    {
        int count = 0;
        var watchdog = Watchdog.Start(TimeSpan.FromMilliseconds(200), () => Interlocked.Increment(ref count));
        watchdog.Dispose();
        await Task.Delay(500);
        Check.Equal(0, count);
        Check.True(!watchdog.Expired, "not expired");
        watchdog.Dispose();
    }
}
