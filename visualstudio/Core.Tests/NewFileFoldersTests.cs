using System;
using System.Collections.Generic;
using TUnit.Core;

namespace RunicArtifex.DevKit.VisualStudio.Core.Tests;

/// <summary>
/// The missing folder of the file Extract Group creates (Translations 0.14, protocol 2). The actions are shaped as the
/// server's <c>codeAction/resolve</c> answers them: an edit of the source, a <c>create</c>, and an edit of the new file.
/// </summary>
public class NewFileFoldersTests
{
    private const string Source = "file:///c%3A/work/app/translations/en.rmf2";
    private const string Created = "file:///c%3A/work/app/translations/counter/en.rmf2";
    private static readonly string SourcePath = InlineEmptiedFiles.PathOf(Source)!;
    private static readonly string Project = NewFileFolders.Parent(SourcePath)!;
    private static readonly string App = NewFileFolders.Parent(Project)!;
    private static readonly string Counter = NewFileFolders.Child(Project, "counter");

    private static string Edit(string uri, string text) =>
        $$"""{ "textDocument": { "uri": "{{uri}}", "version": null }, "edits": [ { "range": { "start": { "line": 0, "character": 0 }, "end": { "line": 0, "character": 0 } }, "newText": "{{text}}" } ] }""";

    private static string Action(params string[] changes) =>
        $$"""{ "title": "Extract group 'counter' to counter/en.rmf2", "kind": "refactor.extract", "edit": { "documentChanges": [ {{string.Join(", ", changes)}} ] } }""";

    private static string Create(string uri) => $$"""{ "kind": "create", "uri": "{{uri}}" }""";

    private static readonly string Extract = Action(Edit(Source, ""), Create(Created), Edit(Created, "heading = Counter View\\n"));

    private sealed class Disk : IFolderState
    {
        public readonly HashSet<string> Folders = new(StringComparer.OrdinalIgnoreCase);
        public readonly HashSet<string> Files = new(StringComparer.OrdinalIgnoreCase);
        public bool DirectoryExists(string path) => Folders.Contains(path);
        public bool FileExists(string path) => Files.Contains(path);
    }

    private static Disk Workspace()
    {
        var disk = new Disk();
        for (string? folder = Project; !string.IsNullOrEmpty(folder); folder = NewFileFolders.Parent(folder)) disk.Folders.Add(folder!);
        disk.Files.Add(SourcePath);
        disk.Files.Add(NewFileFolders.Child(Project, "runic.json"));
        return disk;
    }

    [Test]
    public void A_missing_folder_inside_the_Translations_project_is_created()
    {
        var plan = NewFileFolders.Plan(Extract, null, Workspace());
        Check.Equal(NewFileFolderOutcome.CreateFolders, plan.Outcome);
        Check.Same(new[] { Counter }, plan.Folders);
        Check.Same(new[] { NewFileFolders.Child(Counter, "en.rmf2") }, plan.Files);
        Check.True(NewFileFolders.Created(plan).StartsWith("Extract group 'counter' to counter/en.rmf2: created "), NewFileFolders.Created(plan));
    }

    [Test]
    public void Nested_missing_folders_are_created_parents_first()
    {
        var plan = NewFileFolders.Plan(Action(Edit(Source, ""), Create("file:///c%3A/work/app/translations/a/b/en.rmf2")), null, Workspace());
        Check.Equal(NewFileFolderOutcome.CreateFolders, plan.Outcome);
        Check.Same(new[] { NewFileFolders.Child(Project, "a"), NewFileFolders.Child(NewFileFolders.Child(Project, "a"), "b") }, plan.Folders);
    }

    [Test]
    public void An_existing_folder_or_an_edit_without_new_files_applies_as_it_is()
    {
        var disk = Workspace(); disk.Folders.Add(Counter);
        Check.Equal(NewFileFolderOutcome.Apply, NewFileFolders.Plan(Extract, null, disk).Outcome);
        Check.Equal(NewFileFolderOutcome.Apply, NewFileFolders.Plan(Action(Edit(Source, "x")), null, Workspace()).Outcome);
        Check.Equal(NewFileFolderOutcome.Apply, NewFileFolders.Plan("""{ "title": "Inline", "data": {} }""", null, Workspace()).Outcome, "an unresolved action");
        Check.Equal(NewFileFolderOutcome.Apply, NewFileFolders.Plan("not json", null, Workspace()).Outcome);
    }

    [Test]
    public void The_solution_folder_is_a_root_too()
    {
        // A source without runic.json above it: only the solution's folder allows the new folder.
        var disk = Workspace(); disk.Files.Remove(NewFileFolders.Child(Project, "runic.json"));
        Check.Equal(NewFileFolderOutcome.Refuse, NewFileFolders.Plan(Extract, null, disk).Outcome);
        Check.Equal(NewFileFolderOutcome.CreateFolders, NewFileFolders.Plan(Extract, App, disk).Outcome);
    }

    [Test]
    public void A_folder_outside_the_roots_refuses_the_whole_edit()
    {
        var outside = Action(Edit(Source, ""), Create("file:///c%3A/elsewhere/counter/en.rmf2"), Edit("file:///c%3A/elsewhere/counter/en.rmf2", "x"));
        var plan = NewFileFolders.Plan(outside, App, Workspace());
        Check.Equal(NewFileFolderOutcome.Refuse, plan.Outcome);
        Check.True(plan.Folders.Count == 0, "nothing is created");
        Check.True(plan.Reason!.StartsWith("Runic Translations did not apply \"Extract group 'counter' to counter/en.rmf2\": it creates ")
            && plan.Reason.Contains("outside the solution and the Translations project") && plan.Reason.EndsWith("Nothing was changed."), plan.Reason);
        // One file outside refuses the edit even when another one's folder could be created.
        Check.Equal(NewFileFolderOutcome.Refuse, NewFileFolders.Plan(Action(Edit(Source, ""), Create(Created), Create("file:///c%3A/elsewhere/x/en.rmf2")), App, Workspace()).Outcome);
    }

    [Test]
    public void Drive_roots_dot_segments_and_non_file_uris_are_refused()
    {
        var disk = Workspace(); disk.Files.Add(NewFileFolders.Child(NewFileFolders.Parent(App)!, "runic.json"));
        Check.Equal(NewFileFolderOutcome.Refuse, NewFileFolders.Plan(Action(Edit(Source, ""), Create("untitled:counter/en.rmf2")), App, Workspace()).Outcome);
        Check.Equal(NewFileFolderOutcome.Refuse, NewFileFolders.Plan(Action(Edit(Source, ""), Create("file:///c%3A/top/en.rmf2")), "c:\\", Workspace()).Outcome, "a drive is no root");
        Check.True(!NewFileFolders.Inside("c:/work/app/../../x", "c:/work/app"), "dot segments");
        Check.True(!NewFileFolders.Inside("c:/work/app", "c:/work/app"), "the root itself is not inside");
        Check.True(!NewFileFolders.Inside("c:/work/apple/x", "c:/work/app"), "a sibling with the same prefix");
        Check.True(NewFileFolders.Inside("C:\\Work\\App\\x", "c:/work/app"), "case and separators");
        Check.True(!NewFileFolders.Inside("\\\\server\\share\\x", "\\\\server"), "a server is no root");
    }

    [Test]
    public void A_file_in_the_way_refuses_the_edit()
    {
        var disk = Workspace(); disk.Files.Add(Counter);
        var plan = NewFileFolders.Plan(Extract, null, disk);
        Check.Equal(NewFileFolderOutcome.Refuse, plan.Outcome);
        Check.True(plan.Reason!.Contains("is a file, not a folder"), plan.Reason);
    }

    [Test]
    public void A_rename_target_and_applyEdit_parameters_are_planned_too()
    {
        string rename = $$"""{ "label": "Move", "edit": { "documentChanges": [ { "kind": "rename", "oldUri": "{{Source}}", "newUri": "{{Created}}" } ] } }""";
        var plan = NewFileFolders.Plan(rename, null, Workspace());
        Check.Equal(NewFileFolderOutcome.CreateFolders, plan.Outcome);
        Check.Equal("Move", plan.Title);
        Check.True(NewFileFolders.Follows(NewFileFolders.ApplyEdit) && NewFileFolders.Follows(NewFileFolders.ResolveCodeAction) && !NewFileFolders.Follows("textDocument/rename"), "methods");
    }

    [Test]
    public void A_failed_folder_names_the_folder_and_the_reason()
    {
        var plan = NewFileFolders.Plan(Extract, null, Workspace());
        string message = NewFileFolders.CreateFailed(plan, Counter, "Access to the path is denied");
        Check.True(message.Contains(Counter) && message.Contains("Access to the path is denied.") && message.EndsWith("Nothing was changed."), message);
    }
}
