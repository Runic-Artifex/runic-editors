using System;
using System.Collections.Generic;
using System.Linq;
using TUnit.Core;

namespace RunicArtifex.DevKit.VisualStudio.Core.Tests;

/// <summary>runic-editors#44: which of Visual Studio's JavaScript debug adapters DevKit may end.</summary>
public class JsDebugAdaptersTests
{
    private const string Root = @"C:\Program Files\Microsoft Visual Studio\18\Community";
    private const string Me = "S-1-5-21-1-2-3-1001";
    private static readonly DateTime T0 = new(2026, 11, 19, 10, 0, 0, DateTimeKind.Utc);

    private static string AdapterLine(string root = Root) =>
        $"\"{root}\\MSBuild\\Microsoft\\VisualStudio\\NodeJs\\node.exe\" \"{root}\\Common7\\IDE\\Extensions\\Microsoft\\JavaScript\\jsdiagnostics\\debugger\\debugAdapter\\src\\vsDebugServer.js\" --stdio";

    private static ProcessEntry Devenv(int id, int minutes) => new(id, 1, "devenv.exe", $"\"{Root}\\Common7\\IDE\\devenv.exe\"", T0.AddMinutes(minutes), Me);
    private static ProcessEntry Adapter(int id, int parent, int minutes, string? owner = Me, string root = Root) => new(id, parent, "node.exe", AdapterLine(root), T0.AddMinutes(minutes), owner);

    [Test]
    public void The_adapter_is_recognised_by_its_script()
    {
        Check.True(JsDebugAdapters.IsAdapter(AdapterLine()), "vsDebugServer.js under jsdiagnostics");
        Check.True(JsDebugAdapters.IsAdapter(AdapterLine().Replace('\\', '/')), "forward slashes");
        Check.True(!JsDebugAdapters.IsAdapter("node.exe C:\\work\\vsDebugServer.js"), "another script of that name");
        Check.True(!JsDebugAdapters.IsAdapter("node.exe vite"), "the app's Vite");
        Check.True(!JsDebugAdapters.IsAdapter(null), "no command line (another user's process)");
    }

    [Test]
    public void The_installation_root_comes_from_devenv_s_path()
    {
        Check.Equal(Root, JsDebugAdapters.InstallationRoot(Root + @"\Common7\IDE\devenv.exe"));
        Check.Equal<string?>(null, JsDebugAdapters.InstallationRoot(@"C:\tools\devenv.exe"));
        Check.True(JsDebugAdapters.BelongsToInstallation(AdapterLine(), Root), "same installation");
        Check.True(!JsDebugAdapters.BelongsToInstallation(AdapterLine(@"C:\Program Files\Microsoft Visual Studio\18\Preview"), Root), "the Preview installation");
        Check.True(!JsDebugAdapters.BelongsToInstallation(AdapterLine(Root + "X"), Root), "a sibling folder that starts the same");
    }

    [Test]
    public void Adapters_are_sorted_into_own_orphaned_unclear_and_another_live_instance_s()
    {
        var processes = new List<ProcessEntry>
        {
            Devenv(100, 0),
            Adapter(101, 100, 1),                       // this Visual Studio's
            new(102, 100, "conhost.exe", null, T0.AddMinutes(1), Me),
            Adapter(103, 102, 2),                       // a grandchild of this Visual Studio
            Devenv(200, 0),
            Adapter(201, 200, 1),                       // another running Visual Studio's: left alone
            Adapter(301, 300, 1),                       // parent 300 gone: orphaned
            new(400, 1, "notepad.exe", "notepad", T0.AddMinutes(5), Me),
            Adapter(401, 400, 1),                       // parent ID 400 reused by a later process: orphaned
            Adapter(501, 500, 1, owner: "S-1-5-21-9-9-9-1002"),   // another user's
            Adapter(601, 600, 1, owner: null),          // unknown owner
            Adapter(701, 700, 1, root: @"C:\Program Files\Microsoft Visual Studio\18\Preview"),
        };
        var census = JsDebugAdapters.Census(processes, 100, Me, Root);
        Check.Same(new[] { 101, 103 }, census.Own.Select(process => process.ProcessId), "own");
        Check.Same(new[] { 301, 401 }, census.Orphaned.Select(process => process.ProcessId), "orphaned");
        Check.Same(new[] { 501, 601, 701 }, census.Unclear.Select(process => process.ProcessId), "unclear");
    }

    [Test]
    public void Without_the_user_s_SID_nothing_is_orphaned()
    {
        var census = JsDebugAdapters.Census(new[] { Adapter(301, 300, 1) }, 100, null, Root);
        Check.Equal(0, census.Orphaned.Count);
        Check.Equal(1, census.Unclear.Count);
    }

    [Test]
    public void A_hung_attach_ends_the_adapter_it_started_or_else_all_of_this_instance_s()
    {
        var old = Adapter(101, 100, 1);
        var started = Adapter(102, 100, 3);
        var before = new[] { 101, 4242 };
        Check.Same(new[] { 102 }, JsDebugAdapters.ToEndForHungAttach(new[] { old, started }, before).Select(process => process.ProcessId), "the new one");
        Check.Same(new[] { 101 }, JsDebugAdapters.ToEndForHungAttach(new[] { old }, before).Select(process => process.ProcessId), "a reused one");
        Check.Equal(0, JsDebugAdapters.ToEndForHungAttach(Array.Empty<ProcessEntry>(), before).Count);
    }

    [Test]
    public void The_messages_name_the_processes()
    {
        var one = new[] { Adapter(301, 300, 1) };
        Check.True(JsDebugAdapters.EndedOrphansMessage(one).Contains("1 JavaScript debug adapter (node.exe)") && JsDebugAdapters.EndedOrphansMessage(one).Contains("process 301"), "ended orphans");
        string unclear = JsDebugAdapters.UnclearMessage(new[] { Adapter(501, 500, 1), Adapter(502, 500, 1) });
        Check.True(unclear.StartsWith("2 JavaScript debug adapters", StringComparison.Ordinal) && unclear.Contains("they belong") && unclear.Contains("process 501, 502"), unclear);
        Check.True(JsDebugAdapters.EndedForHungAttachMessage("Closing", Array.Empty<ProcessEntry>()).Contains("cannot end"), "none found");
    }
}
