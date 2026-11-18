using System;

namespace RunicArtifex.DevKit.VisualStudio.Core;

/// <summary>
/// What DevKit says when the Translations language server refuses F2 (runic-editors#32). The server answers a rename it
/// cannot carry out (an invalid name, a catalog that does not compile) with a JSON-RPC error and its reason, as VS Code
/// shows it; Visual Studio's language client closes the rename box without a message. The middle layer
/// (<c>Translations/RenameRefusals.cs</c>) sees the error and shows the text decided here in an InfoBar, the status bar
/// and the Runic Translations Output pane.
/// </summary>
public static class RenameRefusal
{
    public const string Rename = "textDocument/rename";
    public const string PrepareRename = "textDocument/prepareRename";
    /// <summary>LSP RequestCancelled: Visual Studio cancelled the request itself, nothing to report.</summary>
    public const int RequestCancelled = -32800;
    /// <summary>LSP ContentModified: the files changed while the request was pending (runic-editors#9).</summary>
    public const int ContentModified = -32801;
    private const int MaximumReasonLength = 400;

    /// <summary>Whether the middle layer follows <paramref name="method"/>.</summary>
    public static bool Follows(string method) => method is Rename or PrepareRename;

    /// <summary>
    /// The message for a refused rename request, or null when there is nothing to tell: another method, or a request
    /// that Visual Studio cancelled (<paramref name="cancelled"/>, or the server's RequestCancelled answer).
    /// </summary>
    /// <param name="code">The JSON-RPC error code, when the error carries one.</param>
    /// <param name="reason">The server's error message.</param>
    public static string? Message(string method, int? code, string? reason, bool cancelled = false)
    {
        if (!Follows(method) || cancelled || code == RequestCancelled) return null;
        if (code == ContentModified)
            return "Runic Translations: the rename was not carried out because the files changed while the language server prepared it. Press F2 again.";
        return "Runic Translations refused the rename: " + Reason(reason);
    }

    /// <summary>The server's reason on one line, shortened for the InfoBar; the Output pane gets the same text.</summary>
    public static string Reason(string? reason)
    {
        string text = string.Join(" ", (reason ?? "").Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)).Trim();
        if (text.Length == 0) return "the language server gave no reason. See the Runic Translations Output pane with the trace on.";
        if (text.Length > MaximumReasonLength) text = text.Substring(0, MaximumReasonLength - 1).TrimEnd() + "…";
        return text.EndsWith(".", StringComparison.Ordinal) || text.EndsWith("…", StringComparison.Ordinal) || text.EndsWith("?", StringComparison.Ordinal) || text.EndsWith("!", StringComparison.Ordinal) ? text : text + ".";
    }
}
