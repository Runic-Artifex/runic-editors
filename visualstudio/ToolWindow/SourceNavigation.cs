using System;
using System.IO;
using Microsoft.VisualStudio;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.TextManager.Interop;
using RunicArtifex.DevKit.VisualStudio.Core;

namespace RunicArtifex.DevKit.VisualStudio.ToolWindow;

/// <summary>
/// Open C# Member: opens the development source map's file and puts the caret at its 1-based line and column
/// (VS Code: <c>sdk.ts</c> <c>openLocation</c>). The map already has file:line, so no Roslyn symbol lookup is needed
/// (PARITY §2.2, "Go to C# member").
/// </summary>
internal static class SourceNavigation
{
    public const string NoLocation = "This entry has no source location; build the project with a PDB.";

    public static void Open(SourceLocation? location)
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        if (location == null || string.IsNullOrEmpty(location.File)) throw new InvalidOperationException(NoLocation);
        if (!File.Exists(location.File)) throw new InvalidOperationException($"Could not open {location}: the file does not exist.");
        VsShellUtilities.OpenDocument(ServiceProvider.GlobalProvider, location.File, Guid.Empty, out _, out _, out _, out IVsTextView view);
        if (view == null) return;
        int line = Math.Max(0, location.Line - 1);
        int column = Math.Max(0, location.Column - 1);
        // A stale map can point past the line's end; then the caret goes to the line's start.
        if (ErrorHandler.Failed(view.SetCaretPos(line, column))) view.SetCaretPos(line, 0);
        view.CenterLines(line, 1);
    }
}
