using System;
using System.Collections.Generic;
using System.Linq;

namespace RunicArtifex.DevKit.VisualStudio.Core;

/// <summary>What Visual Studio knows about a file when <see cref="InlineEmptiedFiles"/> decides.</summary>
public interface IEmptiedFileState
{
    /// <summary>Whether the file is open with unsaved changes.</summary>
    bool IsDirty(string path);

    /// <summary>
    /// The file's bytes on disk when it holds at most <paramref name="limit"/> bytes, an array longer than the limit
    /// when it holds more (its content does not matter then), or null when it does not exist.
    /// </summary>
    byte[]? Read(string path, int limit);
}

/// <summary>Why <see cref="InlineEmptiedFiles.AfterSave"/> deletes, keeps or waits on a file.</summary>
public enum EmptiedFileOutcome
{
    /// <summary>Applied, saved, empty on disk, and every other file of the edit is saved: delete it.</summary>
    Delete,
    /// <summary>The file or another file of the edit has unsaved changes; decide at a later save.</summary>
    Waiting,
    /// <summary>The file is not empty on disk any more (undone, or edited); it stays and is no longer followed.</summary>
    KeptNotEmpty,
    /// <summary>The file no longer exists.</summary>
    Gone,
}

/// <summary>A decision of <see cref="InlineEmptiedFiles.AfterSave"/>.</summary>
public sealed class EmptiedFileDecision
{
    internal EmptiedFileDecision(string path, EmptiedFileOutcome outcome) { Path = path; Outcome = outcome; }
    public string Path { get; }
    public EmptiedFileOutcome Outcome { get; }
}

/// <summary>
/// The child file an Inline Resource refactoring empties in Visual Studio. Visual Studio 2026 does not advertise the
/// <c>delete</c> resource operation, so the language server of Translations 0.14 (protocol 2) moves the child's
/// resources into the ancestor file and empties the child with a text edit instead of deleting it (its action title
/// says "leaves en.rmf2 empty"); a server-side delete could lose moved resources that are not saved yet. This class
/// decides when the extension deletes that file after all:
/// <list type="number">
/// <item><see cref="Offered"/>: a resolved <c>refactor.inline</c> action names the file its edit empties (one
/// TextDocumentEdit that replaces the whole document, from 0:0, with nothing) and the other files it edits.</item>
/// <item><see cref="Changed"/>: the client's <c>textDocument/didChange</c> for that file replaces exactly that range with
/// nothing, within <see cref="ApplyWindow"/> of the resolve: the edit was applied, not only previewed.</item>
/// <item><see cref="AfterSave"/>: after a save of any of the edit's files, the file is deleted only when it has no unsaved
/// changes, is empty on disk (no bytes, or only a UTF-8 byte order mark), and every other file of the edit is saved,
/// so the moved resources are on disk first. A file that is not empty is never deleted.</item>
/// </list>
/// Only the exact file the action's edit emptied is ever returned. Paths compare case-insensitively (Windows).
/// </summary>
public sealed class InlineEmptiedFiles
{
    /// <summary>How long after the resolve the edit may be applied.</summary>
    public static readonly TimeSpan ApplyWindow = TimeSpan.FromMinutes(2);
    private const int MaxFollowed = 32;
    private static readonly StringComparer Paths = StringComparer.OrdinalIgnoreCase;

    private readonly object gate = new();
    private readonly List<Entry> entries = new();

    private sealed class Entry
    {
        public string Path = "";
        public int EndLine, EndCharacter;
        public string[] Related = System.Array.Empty<string>();
        public DateTimeOffset OfferedAt;
        public bool Applied;
    }

    /// <summary>The emptied file of a resolved code action, with its edit's range end and the edit's other files.</summary>
    public sealed class EmptiedEdit
    {
        internal EmptiedEdit(string path, int endLine, int endCharacter, string[] related)
        { Path = path; EndLine = endLine; EndCharacter = endCharacter; Related = related; }
        public string Path { get; }
        public int EndLine { get; }
        public int EndCharacter { get; }
        public IReadOnlyList<string> Related { get; }
    }

    /// <summary>The files a resolved <c>refactor.inline</c> code action (JSON) empties instead of deleting.</summary>
    public static IReadOnlyList<EmptiedEdit> Read(string actionJson)
    {
        var action = Json.Object(TryParse(actionJson));
        if (Json.String(action, "kind") != "refactor.inline") return System.Array.Empty<EmptiedEdit>();
        var changes = Json.Array(Json.Get(Json.Object(Json.Get(action, "edit")), "documentChanges"));
        if (changes == null) return System.Array.Empty<EmptiedEdit>();
        var deleted = new HashSet<string>(Paths);
        var edited = new List<(string Path, Dictionary<string, object?>? Only)>();
        foreach (var change in changes.Select(Json.Object))
        {
            if (change == null) continue;
            string? kind = Json.String(change, "kind");
            if (kind == "delete" && PathOf(Json.String(change, "uri")) is string gone) { deleted.Add(gone); continue; }
            if (kind != null) continue;
            string? path = PathOf(Json.String(Json.Object(Json.Get(change, "textDocument")), "uri"));
            var edits = Json.Array(Json.Get(change, "edits"));
            if (path == null || edits == null) continue;
            edited.Add((path, edits.Count == 1 ? Json.Object(edits[0]) : null));
        }
        var result = new List<EmptiedEdit>();
        foreach (var (path, only) in edited)
        {
            if (only == null || Json.Get(only, "newText") is not "" || deleted.Contains(path)) continue;
            var range = Json.Object(Json.Get(only, "range"));
            if (Position(Json.Get(range, "start")) is not (0, 0) || Position(Json.Get(range, "end")) is not (int line, int character)) continue;
            var related = edited.Select(item => item.Path).Where(other => !Paths.Equals(other, path)).Distinct(Paths).ToArray();
            result.Add(new EmptiedEdit(path, line, character, related));
        }
        return result;
    }

    /// <summary>Follows the files a resolved code action (JSON) empties. True when it follows any.</summary>
    public bool Offered(string actionJson, DateTimeOffset now)
    {
        var emptied = Read(actionJson);
        if (emptied.Count == 0) return false;
        lock (gate)
        {
            Expire(now);
            foreach (var edit in emptied)
            {
                entries.RemoveAll(entry => Paths.Equals(entry.Path, edit.Path));
                entries.Add(new Entry { Path = edit.Path, EndLine = edit.EndLine, EndCharacter = edit.EndCharacter, Related = edit.Related.ToArray(), OfferedAt = now });
            }
            while (entries.Count > MaxFollowed) entries.RemoveAt(0);
        }
        return true;
    }

    /// <summary>
    /// A <c>textDocument/didChange</c> notification (JSON). True when it applied a followed file's emptying edit, so the
    /// file is now decided at its saves.
    /// </summary>
    public bool Changed(string didChangeJson, DateTimeOffset now)
    {
        var notification = Json.Object(TryParse(didChangeJson));
        string? path = PathOf(Json.String(Json.Object(Json.Get(notification, "textDocument")), "uri"));
        var changes = Json.Array(Json.Get(notification, "contentChanges"));
        if (path == null || changes == null) return false;
        lock (gate)
        {
            Expire(now);
            var entry = entries.FirstOrDefault(item => !item.Applied && Paths.Equals(item.Path, path));
            if (entry == null) return false;
            foreach (var change in changes.Select(Json.Object))
            {
                if (change == null || Json.Get(change, "text") is not "") continue;
                var range = Json.Object(Json.Get(change, "range"));
                // A full-document change (no range) to "" also empties the file.
                bool whole = range == null ||
                    (Position(Json.Get(range, "start")) is (0, 0) && Position(Json.Get(range, "end")) is (int line, int character) &&
                     line == entry.EndLine && character == entry.EndCharacter);
                if (whole) { entry.Applied = true; return true; }
            }
            return false;
        }
    }

    /// <summary>Whether a resolved action's edit has not been applied yet, so <see cref="Changed"/> needs the notifications.</summary>
    public bool AwaitsApply
    {
        get { lock (gate) return entries.Any(entry => !entry.Applied); }
    }

    /// <summary>Whether a save of <paramref name="path"/> concerns an applied edit (its emptied file or another of its files).</summary>
    public bool IsFollowed(string path)
    {
        lock (gate) return entries.Any(entry => entry.Applied && (Paths.Equals(entry.Path, path) || entry.Related.Contains(path, Paths)));
    }

    /// <summary>
    /// Decides, after <paramref name="savedPath"/> was saved, for each applied edit that involves it. A
    /// <see cref="EmptiedFileOutcome.Delete"/> is final: the file is no longer followed, and the caller deletes it after
    /// checking <see cref="IsEmpty"/> and its unsaved state once more.
    /// </summary>
    public IReadOnlyList<EmptiedFileDecision> AfterSave(string savedPath, IEmptiedFileState state)
    {
        var decisions = new List<EmptiedFileDecision>();
        lock (gate)
        {
            foreach (var entry in entries.Where(item => item.Applied && (Paths.Equals(item.Path, savedPath) || item.Related.Contains(savedPath, Paths))).ToList())
            {
                var outcome = Decide(entry, state);
                if (outcome != EmptiedFileOutcome.Waiting) entries.Remove(entry);
                decisions.Add(new EmptiedFileDecision(entry.Path, outcome));
            }
        }
        return decisions;
    }

    private static EmptiedFileOutcome Decide(Entry entry, IEmptiedFileState state)
    {
        bool dirty = state.IsDirty(entry.Path);
        var bytes = state.Read(entry.Path, 3);
        if (bytes == null) return dirty ? EmptiedFileOutcome.Waiting : EmptiedFileOutcome.Gone;
        if (dirty) return EmptiedFileOutcome.Waiting;
        if (!IsEmpty(bytes)) return EmptiedFileOutcome.KeptNotEmpty;
        return entry.Related.Any(state.IsDirty) ? EmptiedFileOutcome.Waiting : EmptiedFileOutcome.Delete;
    }

    /// <summary>Whether a file's bytes are empty: none, or only a UTF-8 byte order mark.</summary>
    public static bool IsEmpty(byte[] bytes) =>
        bytes.Length == 0 || (bytes.Length == 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF);

    private void Expire(DateTimeOffset now) => entries.RemoveAll(entry => !entry.Applied && now - entry.OfferedAt > ApplyWindow);

    private static (int Line, int Character)? Position(object? value)
    {
        var position = Json.Object(value);
        return Json.Number(position, "line") is double line && Json.Number(position, "character") is double character ? ((int)line, (int)character) : null;
    }

    /// <summary>The local path of a <c>file:</c> URI (Visual Studio sends <c>file:///c%3A/...</c>), or null.</summary>
    public static string? PathOf(string? uri)
    {
        if (uri == null) return null;
        // An escaped drive colon reads as a plain path segment; unescape it so both spellings name the same file.
        var drive = System.Text.RegularExpressions.Regex.Match(uri, "^file:///([A-Za-z])%3[Aa]", System.Text.RegularExpressions.RegexOptions.CultureInvariant);
        if (drive.Success) uri = "file:///" + drive.Groups[1].Value + ":" + uri.Substring(drive.Length);
        if (!Uri.TryCreate(uri, UriKind.Absolute, out var parsed) || !parsed.IsFile) return null;
        return parsed.LocalPath;
    }

    private static object? TryParse(string json)
    {
        try { return Json.Parse(json); }
        catch (FormatException) { return null; }
    }
}
