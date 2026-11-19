using System.Collections.Generic;
using TUnit.Core;

namespace RunicArtifex.DevKit.VisualStudio.Core.Tests;

/// <summary>
/// "Break all processes when one process breaks" while DevKit debugs a WebView2 page with its .NET Window: off for the
/// session, on again afterwards, never against the user's own choice, and recovered after a Visual Studio that ended.
/// </summary>
public class BreakAllProcessesTests
{
    private sealed class Options
    {
        public bool BreakAll = true;
        public bool Pending;
        public readonly List<string> Writes = new();

        public BreakAllProcessesGuard Guard() => new(
            () => BreakAll, value => { BreakAll = value; Writes.Add("option=" + value); },
            () => Pending, value => { Pending = value; Writes.Add("pending=" + value); });
    }

    [Test]
    public void Applies_to_WebView2_pages_only()
    {
        Check.True(BreakAllProcessesGuard.AppliesTo("webview2"), "WebView2");
        Check.True(BreakAllProcessesGuard.AppliesTo("WebView2"), "case");
        Check.True(!BreakAllProcessesGuard.AppliesTo("chromium"), "CS-WebUI's Chromium is its own top-level window");
        Check.True(!BreakAllProcessesGuard.AppliesTo(null), "none");
    }

    [Test]
    public void A_session_turns_the_option_off_and_its_end_turns_it_on()
    {
        var options = new Options();
        var guard = options.Guard();
        Check.True(guard.Suspend(), "it was on");
        Check.Equal(false, options.BreakAll);
        Check.True(guard.IsSuspended, "suspended");
        Check.Same(new[] { "pending=True", "option=False" }, options.Writes, "the marker first, so a crash still recovers");

        Check.True(!guard.Suspend(), "a second attach in the same session changes nothing");
        Check.True(guard.Restore(), "the session ended");
        Check.Equal(true, options.BreakAll);
        Check.Equal(false, options.Pending);
        Check.True(!guard.IsSuspended, "no longer suspended");
        Check.True(!guard.Restore(), "a second end changes nothing");
    }

    [Test]
    public void An_option_the_user_turned_off_stays_off()
    {
        var options = new Options { BreakAll = false };
        var guard = options.Guard();
        Check.True(!guard.Suspend(), "already off");
        Check.True(!guard.Restore(), "nothing to restore");
        Check.Equal(false, options.BreakAll);
        Check.Equal(0, options.Writes.Count);
    }

    [Test]
    public void An_option_the_user_turned_on_during_the_session_stays_on()
    {
        var options = new Options();
        var guard = options.Guard();
        guard.Suspend();
        options.BreakAll = true;
        Check.True(!guard.Restore(), "on already");
        Check.Equal(true, options.BreakAll);
        Check.Equal(false, options.Pending, "the marker is cleared");
    }

    [Test]
    public void A_Visual_Studio_that_ended_during_a_session_is_recovered_on_the_next_load()
    {
        var options = new Options { BreakAll = false, Pending = true };
        var guard = options.Guard();
        Check.True(guard.Recover(), "recovered");
        Check.Equal(true, options.BreakAll);
        Check.Equal(false, options.Pending);
        Check.True(!guard.Recover(), "once");

        var turnedOnByHand = new Options { BreakAll = true, Pending = true };
        Check.True(!turnedOnByHand.Guard().Recover(), "on already");
        Check.Equal(false, turnedOnByHand.Pending, "the marker is cleared");

        var clean = new Options { BreakAll = false };
        Check.True(!clean.Guard().Recover(), "no marker: the user's choice");
        Check.Equal(false, clean.BreakAll);
    }

    [Test]
    public void Recover_leaves_a_session_of_this_Visual_Studio_alone()
    {
        var options = new Options();
        var guard = options.Guard();
        guard.Suspend();
        Check.True(!guard.Recover(), "this Visual Studio's own session");
        Check.Equal(false, options.BreakAll);
        Check.True(guard.Restore(), "its end restores");
    }
}

/// <summary>runic-editors#43: Break All during Debug the Window stops only the .NET Window.</summary>
public class BreakAllCommandTests
{
    [Test]
    public void Only_Debug_BreakAll_is_redirected()
    {
        Check.True(BreakAllCommand.Is(DevGuard.LaunchCommandSet, BreakAllCommand.BreakAll), "Debug.BreakAll");
        Check.True(!BreakAllCommand.Is(DevGuard.LaunchCommandSet, DevGuard.StartCommand), "Debug.Start");
        Check.True(!BreakAllCommand.Is(System.Guid.Empty, BreakAllCommand.BreakAll), "another command set");
    }

    [Test]
    public void The_Window_is_the_target_while_its_page_is_debugged_too()
    {
        Check.Equal<int?>(4242, BreakAllCommand.Target(new[] { 4242, 7 }, new[] { 4242 }), "Window and page");
        Check.Equal<int?>(null, BreakAllCommand.Target(new[] { 4242 }, new[] { 4242 }), "only the Window: Break All as usual");
        Check.Equal<int?>(null, BreakAllCommand.Target(new[] { 1, 7 }, new[] { 4242 }), "a session Debug the Window did not start");
        Check.Equal<int?>(null, BreakAllCommand.Target(new[] { 4242, 7 }, System.Array.Empty<int>()), "no WebView2 Window");
        Check.True(BreakAllCommand.BrokeWindowOnlyMessage(4242).Contains("process 4242"), "message");
    }
}
