using System;
using System.Collections.Generic;
using System.Linq;
using TUnit.Core;

namespace RunicArtifex.DevKit.VisualStudio.Core.Tests;

/// <summary>
/// The client-side delete of the child file Inline Resource empties for Visual Studio (Translations 0.14, protocol 2,
/// a client without the <c>delete</c> resource operation). The actions are shaped as the server's
/// <c>codeAction/resolve</c> answers them (Rmf2LanguageServer.WorkspaceEdit with emptyInsteadOfDelete).
/// </summary>
public class InlineEmptiedFilesTests
{
    private const string Child = "file:///c%3A/work/app/account/en.rmf2";
    private const string Parent = "file:///c%3A/work/app/en.rmf2";
    private static readonly string ChildPath = InlineEmptiedFiles.PathOf(Child)!;
    private static readonly string ParentPath = InlineEmptiedFiles.PathOf(Parent)!;
    private static readonly DateTimeOffset Now = new(2026, 11, 18, 12, 0, 0, TimeSpan.Zero);

    private static string Edit(string uri, int startLine, int startCharacter, int endLine, int endCharacter, string text, int? version = 1) =>
        $$"""{ "textDocument": { "uri": "{{uri}}", "version": {{(version?.ToString() ?? "null")}} }, "edits": [ { "range": { "start": { "line": {{startLine}}, "character": {{startCharacter}} }, "end": { "line": {{endLine}}, "character": {{endCharacter}} } }, "newText": "{{text}}" } ] }""";

    private static string Action(string kind, params string[] changes) =>
        $$"""{ "title": "Inline into ../en.rmf2 (leaves en.rmf2 empty)", "kind": "{{kind}}", "data": { "refactor": "inlineResource" }, "edit": { "documentChanges": [ {{string.Join(", ", changes)}} ] } }""";

    // The server's resolved Inline: the parent gets the moved resources, the child (3 lines) is replaced with nothing.
    private static readonly string Inline = Action("refactor.inline",
        Edit(Parent, 0, 0, 4, 0, "checkout = Checkout\\naccount\\n  heading = Your account\\n", null),
        Edit(Child, 0, 0, 3, 0, ""));

    private static string DidChange(string uri, string change) =>
        $$"""{ "textDocument": { "uri": "{{uri}}", "version": 2 }, "contentChanges": [ {{change}} ] }""";

    private static readonly string Applied = DidChange(Child, """{ "range": { "start": { "line": 0, "character": 0 }, "end": { "line": 3, "character": 0 } }, "rangeLength": 42, "text": "" }""");

    private sealed class Files : IEmptiedFileState
    {
        public readonly Dictionary<string, byte[]> Disk = new(StringComparer.OrdinalIgnoreCase);
        public readonly HashSet<string> Dirty = new(StringComparer.OrdinalIgnoreCase);
        public bool IsDirty(string path) => Dirty.Contains(path);
        public byte[]? Read(string path, int limit) => Disk.TryGetValue(path, out var bytes) ? bytes.Take(limit + 1).ToArray() : null;
    }

    private static Files Saved(byte[] child) => new() { Disk = { [ChildPath] = child, [ParentPath] = "checkout = Checkout\n"u8.ToArray() } };

    private static InlineEmptiedFiles AppliedTracker()
    {
        var tracker = new InlineEmptiedFiles();
        Check.True(tracker.Offered(Inline, Now), "The resolved Inline must be followed");
        Check.True(tracker.Changed(Applied, Now.AddSeconds(1)), "The didChange that empties the child applies it");
        return tracker;
    }

    [Test]
    public void Read_returns_only_the_file_the_inline_edit_empties_with_the_other_files_of_the_edit()
    {
        var emptied = InlineEmptiedFiles.Read(Inline).Single();
        Check.Equal(ChildPath, emptied.Path);
        Check.Equal(3, emptied.EndLine); Check.Equal(0, emptied.EndCharacter);
        Check.Same(new[] { ParentPath }, emptied.Related);
        // Visual Studio sends file:///c%3A/..., the server file:///C:/... for a file it read from disk.
        Check.True(StringComparer.OrdinalIgnoreCase.Equals(ChildPath, InlineEmptiedFiles.PathOf("file:///C:/work/app/account/en.rmf2")), ChildPath);
    }

    [Test]
    public void Read_ignores_other_actions_partial_edits_and_edits_a_client_deletes()
    {
        Check.Equal(0, InlineEmptiedFiles.Read(Action("refactor.extract", Edit(Child, 0, 0, 3, 0, ""))).Count, "Only Inline Resource empties a file");
        Check.Equal(0, InlineEmptiedFiles.Read(Action("refactor.inline", Edit(Parent, 0, 0, 4, 0, "x"), Edit(Child, 1, 0, 3, 0, ""))).Count, "A deletion that does not start at 0:0 does not empty the file");
        Check.Equal(0, InlineEmptiedFiles.Read(Action("refactor.inline", Edit(Parent, 0, 0, 4, 0, "x"), Edit(Child, 0, 0, 3, 0, " "))).Count, "Replaced with text");
        Check.Equal(0, InlineEmptiedFiles.Read(Action("refactor.inline", Edit(Parent, 0, 0, 4, 0, "x"), $$"""{ "kind": "delete", "uri": "{{Child}}" }""")).Count, "A client with delete deletes it itself");
        Check.Equal(0, InlineEmptiedFiles.Read("""{ "title": "Inline", "kind": "refactor.inline" }""").Count, "Unresolved");
        Check.Equal(0, InlineEmptiedFiles.Read("not json").Count, "Not JSON");
    }

    [Test]
    public void An_applied_inline_deletes_the_emptied_file_once_it_and_the_destination_are_saved()
    {
        var tracker = AppliedTracker();
        var files = Saved(Array.Empty<byte>());
        files.Dirty.Add(ParentPath);
        // The child is saved first while the moved resources are still unsaved in the parent: wait.
        var first = tracker.AfterSave(ChildPath, files).Single();
        Check.Equal(EmptiedFileOutcome.Waiting, first.Outcome);
        Check.True(tracker.IsFollowed(ParentPath), "The parent's save decides next");
        files.Dirty.Clear();
        var second = tracker.AfterSave(ParentPath, files).Single();
        Check.Equal(EmptiedFileOutcome.Delete, second.Outcome);
        Check.Equal(ChildPath, second.Path);
        Check.True(!tracker.IsFollowed(ChildPath), "A deleted file is no longer followed");
        Check.Equal(0, tracker.AfterSave(ChildPath, files).Count, "Decided once");
    }

    [Test]
    public void A_file_with_only_a_byte_order_mark_is_empty()
    {
        Check.True(InlineEmptiedFiles.IsEmpty(new byte[] { 0xEF, 0xBB, 0xBF }), "BOM");
        Check.True(!InlineEmptiedFiles.IsEmpty(new byte[] { 0xEF, 0xBB, 0xBF, (byte)'a' }), "BOM and text");
        Check.Equal(EmptiedFileOutcome.Delete, AppliedTracker().AfterSave(ChildPath, Saved(new byte[] { 0xEF, 0xBB, 0xBF })).Single().Outcome);
    }

    [Test]
    public void A_file_that_is_not_empty_or_not_saved_is_never_deleted()
    {
        var tracker = AppliedTracker();
        var unsaved = Saved(Array.Empty<byte>());
        unsaved.Dirty.Add(ChildPath);
        Check.Equal(EmptiedFileOutcome.Waiting, tracker.AfterSave(ChildPath, unsaved).Single().Outcome, "Unsaved child");
        // Undone, or typed into, then saved: kept and no longer followed.
        var kept = tracker.AfterSave(ChildPath, Saved("heading = Mine\n"u8.ToArray())).Single();
        Check.Equal(EmptiedFileOutcome.KeptNotEmpty, kept.Outcome);
        Check.Equal(0, tracker.AfterSave(ChildPath, Saved(Array.Empty<byte>())).Count, "A kept file is not deleted when emptied later");
        var gone = AppliedTracker().AfterSave(ChildPath, new Files()).Single();
        Check.Equal(EmptiedFileOutcome.Gone, gone.Outcome);
    }

    [Test]
    public void A_previewed_inline_or_a_different_change_does_not_count_as_applied()
    {
        var tracker = new InlineEmptiedFiles();
        tracker.Offered(Inline, Now);
        // The light bulb resolves to preview the action; the user empties the file differently and saves.
        Check.True(!tracker.Changed(DidChange(Child, """{ "range": { "start": { "line": 0, "character": 0 }, "end": { "line": 2, "character": 0 } }, "text": "" }"""), Now), "Another range");
        Check.True(!tracker.Changed(DidChange(Child, """{ "range": { "start": { "line": 0, "character": 0 }, "end": { "line": 3, "character": 0 } }, "text": "x" }"""), Now), "Text");
        Check.True(!tracker.Changed(DidChange(Parent, """{ "text": "" }"""), Now), "Another file");
        Check.Equal(0, tracker.AfterSave(ChildPath, Saved(Array.Empty<byte>())).Count, "Not applied");
        Check.True(!tracker.Changed(Applied, Now + InlineEmptiedFiles.ApplyWindow + TimeSpan.FromSeconds(1)), "Too late after the resolve");
        Check.Equal(0, tracker.AfterSave(ChildPath, Saved(Array.Empty<byte>())).Count, "Expired");
        // A full-document change to "" applies it too.
        var full = new InlineEmptiedFiles();
        full.Offered(Inline, Now);
        Check.True(full.Changed(DidChange(Child, """{ "text": "" }"""), Now), "Full-document sync");
        Check.True(!new InlineEmptiedFiles().Offered(Action("refactor.extract", Edit(Child, 0, 0, 3, 0, "")), Now), "Extract is not followed");
    }

    [Test]
    public void A_save_of_an_unrelated_file_decides_nothing()
    {
        var tracker = AppliedTracker();
        Check.Equal(0, tracker.AfterSave(InlineEmptiedFiles.PathOf("file:///c%3A/work/app/de.rmf2")!, Saved(Array.Empty<byte>())).Count, "Unrelated");
        Check.True(tracker.IsFollowed(ChildPath), "Still followed");
    }
}
