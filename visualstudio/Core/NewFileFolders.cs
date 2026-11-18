using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace RunicArtifex.DevKit.VisualStudio.Core;

/// <summary>What the file system holds when <see cref="NewFileFolders"/> decides.</summary>
public interface IFolderState
{
    bool DirectoryExists(string path);
    bool FileExists(string path);
}

/// <summary>What DevKit does with a workspace edit before Visual Studio applies it.</summary>
public enum NewFileFolderOutcome
{
    /// <summary>Every file the edit creates or renames to has its folder: Visual Studio applies the edit as it is.</summary>
    Apply,
    /// <summary>Create <see cref="NewFileFolderPlan.Folders"/> first, then let Visual Studio apply the whole edit.</summary>
    CreateFolders,
    /// <summary>A missing folder that DevKit may not create: apply nothing and say why (<see cref="NewFileFolderPlan.Reason"/>).</summary>
    Refuse,
}

/// <summary>A decision of <see cref="NewFileFolders.Plan"/>.</summary>
public sealed class NewFileFolderPlan
{
    internal NewFileFolderPlan(NewFileFolderOutcome outcome, string title, IReadOnlyList<string> folders, IReadOnlyList<string> files, string? reason)
    { Outcome = outcome; Title = title; Folders = folders; Files = files; Reason = reason; }
    public NewFileFolderOutcome Outcome { get; }
    /// <summary>The code action's title, or the edit's label, for messages.</summary>
    public string Title { get; }
    /// <summary>The missing folders to create, parents first.</summary>
    public IReadOnlyList<string> Folders { get; }
    /// <summary>The files of the edit whose folders are missing.</summary>
    public IReadOnlyList<string> Files { get; }
    public string? Reason { get; }
}

/// <summary>
/// Creates the missing folder of a file that a Translations refactoring creates, before Visual Studio applies its edit.
/// Extract Group (Translations 0.14, protocol 2) moves a group into a new child file such as <c>counter/en.rmf2</c>,
/// with a <c>create</c> resource operation and an edit that fills it. Visual Studio 18 applies the edit that removes the
/// group from the source, but drops the <c>create</c> and the edit of the new file when the file's folder does not
/// exist, without a message, so the group's messages are lost after a save. The middle layer
/// (<c>Translations/NewFolders.cs</c>) reads each resolved code action (and a <c>workspace/applyEdit</c>, should Visual
/// Studio route one through it) and acts on <see cref="Plan"/>:
/// <list type="bullet">
/// <item>Only folders of files the edit names in a <c>create</c> or as the new name of a <c>rename</c> are created.</item>
/// <item>Only inside the solution's folder or a Translations project root (the folder of the <c>runic.json</c> above a
/// file the edit changes), never a drive root, never through <c>.</c> or <c>..</c> segments.</item>
/// <item>Anything else, or a folder that cannot be created, refuses the whole edit: Visual Studio applies none of it, so
/// a group is never removed without its new file.</item>
/// </list>
/// Paths compare case-insensitively (Windows).
/// </summary>
public static class NewFileFolders
{
    public const string ResolveCodeAction = "codeAction/resolve";
    public const string ApplyEdit = "workspace/applyEdit";
    private static readonly StringComparer Paths = StringComparer.OrdinalIgnoreCase;
    private static readonly char[] Separators = { '\\', '/' };

    /// <summary>Whether the middle layer follows <paramref name="method"/>.</summary>
    public static bool Follows(string method) => method is ResolveCodeAction or ApplyEdit;

    /// <summary>
    /// Decides for a resolved code action or <c>workspace/applyEdit</c> parameters (JSON; both carry the WorkspaceEdit in
    /// <c>edit</c>). <paramref name="solutionDirectory"/> is the open solution's folder, if any.
    /// </summary>
    public static NewFileFolderPlan Plan(string json, string? solutionDirectory, IFolderState state)
    {
        Dictionary<string, object?>? owner;
        try { owner = Json.Object(Json.Parse(json)); }
        catch (FormatException) { owner = null; }
        string title = Json.String(owner, "title") ?? Json.String(owner, "label") ?? "The refactoring";
        var changes = Json.Array(Json.Get(Json.Object(Json.Get(owner, "edit")), "documentChanges"));
        if (changes == null) return Applies(title);

        var targets = new List<string>();
        var edited = new List<string>();
        foreach (var change in changes.Select(Json.Object))
        {
            switch (Json.String(change, "kind"))
            {
                case "create":
                    targets.Add(Json.String(change, "uri") ?? "");
                    break;
                case "rename":
                    targets.Add(Json.String(change, "newUri") ?? "");
                    if (InlineEmptiedFiles.PathOf(Json.String(change, "oldUri")) is string old) edited.Add(old);
                    break;
                case null:
                    if (InlineEmptiedFiles.PathOf(Json.String(Json.Object(Json.Get(change, "textDocument")), "uri")) is string path) edited.Add(path);
                    break;
            }
        }
        if (targets.Count == 0) return Applies(title);

        var roots = new List<string>();
        if (Usable(solutionDirectory) && state.DirectoryExists(solutionDirectory!)) roots.Add(solutionDirectory!);
        foreach (var path in edited.Distinct(Paths))
            if (TranslationsRoot(path, state) is string root && !roots.Contains(root, Paths)) roots.Add(root);

        var folders = new List<string>();
        var files = new List<string>();
        foreach (var uri in targets)
        {
            string? file = InlineEmptiedFiles.PathOf(uri);
            if (file == null || !Usable(file)) return Refused(title, $"it names {Describe(uri)}, which is not a local file path.");
            string? folder = Parent(file);
            if (string.IsNullOrEmpty(folder)) return Refused(title, $"{file} has no folder.");
            if (state.DirectoryExists(folder!)) continue;
            if (!roots.Any(root => Inside(folder!, root)))
                return Refused(title, $"it creates {file}, whose folder {folder} does not exist and is outside the solution and the Translations project, so DevKit does not create it.");
            // The missing folders up to the first one that exists, which is inside a root (a root exists).
            var missing = new List<string>();
            for (string? current = folder; !string.IsNullOrEmpty(current) && !state.DirectoryExists(current!); current = Parent(current!))
            {
                if (state.FileExists(current!)) return Refused(title, $"it creates {file}, but {current} is a file, not a folder.");
                missing.Add(current!);
            }
            missing.Reverse();
            foreach (var item in missing) if (!folders.Contains(item, Paths)) folders.Add(item);
            files.Add(file);
        }
        return folders.Count == 0 ? Applies(title)
            : new NewFileFolderPlan(NewFileFolderOutcome.CreateFolders, title, folders, files, null);
    }

    /// <summary>The folder of the nearest <c>runic.json</c> at or above <paramref name="path"/>'s folder, or null.</summary>
    public static string? TranslationsRoot(string path, IFolderState state)
    {
        for (string? current = Parent(path); !string.IsNullOrEmpty(current); current = Parent(current!))
        {
            if (state.FileExists(Child(current!, "runic.json"))) return Usable(current) ? current : null;
        }
        return null;
    }

    /// <summary>Whether <paramref name="path"/> is strictly inside <paramref name="root"/>, compared by path segments.</summary>
    public static bool Inside(string path, string root)
    {
        if (!Usable(path) || !Usable(root)) return false;
        var inner = Segments(path);
        var outer = Segments(root);
        return inner.Length > outer.Length && outer.Select((segment, index) => Paths.Equals(segment, inner[index])).All(same => same);
    }

    /// <summary>The message when DevKit refuses an edit, for the InfoBar and the Runic Translations pane.</summary>
    public static string Refusal(string title, string reason) =>
        $"Runic Translations did not apply \"{title}\": {reason} Nothing was changed.";

    /// <summary>The message when the folders could not be created.</summary>
    public static string CreateFailed(NewFileFolderPlan plan, string folder, string error) =>
        Refusal(plan.Title, $"it creates {string.Join(", ", plan.Files)}, and DevKit could not create the folder {folder}: {RenameRefusal.Reason(error)}");

    /// <summary>The note after DevKit created folders, for the Runic Translations pane.</summary>
    public static string Created(NewFileFolderPlan plan) =>
        $"{plan.Title}: created {string.Join(", ", plan.Folders)} for {string.Join(", ", plan.Files)} (Visual Studio does not create a new file's folder).";

    private static NewFileFolderPlan Applies(string title) =>
        new(NewFileFolderOutcome.Apply, title, Array.Empty<string>(), Array.Empty<string>(), null);

    private static NewFileFolderPlan Refused(string title, string reason) =>
        new(NewFileFolderOutcome.Refuse, title, Array.Empty<string>(), Array.Empty<string>(), Refusal(title, reason));

    private static string Describe(string uri) => uri.Length == 0 ? "no file" : uri.Length > 200 ? uri.Substring(0, 199) + "…" : uri;

    /// <summary>An absolute path without <c>.</c> or <c>..</c> segments that is not a drive or share root.</summary>
    private static bool Usable(string? path)
    {
        if (string.IsNullOrEmpty(path)) return false;
        // Windows paths are absolute on every platform (Core.Tests run on Linux too).
        bool unc = path!.StartsWith(@"\\", StringComparison.Ordinal) || path.StartsWith("//", StringComparison.Ordinal);
        bool drive = path.Length >= 3 && char.IsLetter(path[0]) && path[1] == ':' && (path[2] is '\\' or '/');
        if (!unc && !drive && !Path.IsPathRooted(path)) return false;
        var segments = Segments(path);
        if (segments.Any(segment => segment is "." or "..")) return false;
        // A drive (C:), the file system root or a UNC share (\\server\share) is too wide to be a root.
        int minimum = unc ? 3 : drive ? 2 : 1;
        return segments.Length >= minimum;
    }

    /// <summary>The folder of a Windows or Unix path, either separator on every platform; null above a root.</summary>
    internal static string? Parent(string path)
    {
        string trimmed = path.TrimEnd(Separators);
        int index = trimmed.LastIndexOfAny(Separators);
        if (index < 0) return null;
        string parent = trimmed.Substring(0, index);
        // c:\x → c:\ and /x → /: keep the root's separator.
        if (parent.Length == 0 || (parent.Length == 2 && parent[1] == ':')) parent = trimmed.Substring(0, index + 1);
        return parent.Length == trimmed.Length ? null : parent;
    }

    /// <summary><paramref name="name"/> in <paramref name="folder"/>, with the folder's separator.</summary>
    internal static string Child(string folder, string name)
    {
        char separator = folder.IndexOf('\\') >= 0 || folder.IndexOf('/') < 0 ? '\\' : '/';
        return folder.TrimEnd(Separators) + separator + name;
    }

    private static string[] Segments(string path) => path.Split(Separators, StringSplitOptions.RemoveEmptyEntries);
}
