using TUnit.Core;

namespace RunicArtifex.DevKit.VisualStudio.Core.Tests;

/// <summary>The message for an F2 the Translations language server refuses (runic-editors#32).</summary>
public class RenameRefusalTests
{
    [Test]
    public void A_refused_rename_shows_the_server_reason()
    {
        Check.Equal("Runic Translations refused the rename: The catalog does not compile: RTR0001 at en.rmf2:3.",
            RenameRefusal.Message(RenameRefusal.Rename, -32803, "The catalog does not compile: RTR0001 at en.rmf2:3."));
        Check.Equal("Runic Translations refused the rename: Use an identifier.",
            RenameRefusal.Message(RenameRefusal.Rename, -32602, "Use an identifier"), "a full stop is added");
        Check.Equal("Runic Translations refused the rename: Rename requires a resource name, an MF2 variable or a slot reference.",
            RenameRefusal.Message(RenameRefusal.PrepareRename, null, "Rename requires a resource name, an MF2 variable or a slot reference."), "an error without a code");
    }

    [Test]
    public void The_reason_is_one_line_and_bounded()
    {
        Check.Equal("first second.", RenameRefusal.Reason("first\r\n\r\nsecond"));
        string reason = RenameRefusal.Reason(new string('x', 1000));
        Check.True(reason.Length == 400 && reason.EndsWith("…"), "a long reason is shortened");
        Check.True(RenameRefusal.Reason("  ").StartsWith("the language server gave no reason"), "an empty reason says where to look");
    }

    [Test]
    public void An_outdated_rename_asks_to_press_F2_again()
    {
        string? message = RenameRefusal.Message(RenameRefusal.Rename, RenameRefusal.ContentModified, "Document content changed while the request was pending.");
        Check.True(message != null && message.Contains("files changed") && message.EndsWith("Press F2 again."), $"ContentModified: {message}");
    }

    [Test]
    public void Cancellations_and_other_methods_say_nothing()
    {
        Check.Equal(null, RenameRefusal.Message(RenameRefusal.Rename, RenameRefusal.RequestCancelled, "Request cancelled."));
        Check.Equal(null, RenameRefusal.Message(RenameRefusal.Rename, -32803, "Refused", cancelled: true));
        Check.Equal(null, RenameRefusal.Message("textDocument/hover", -32803, "Refused"));
        Check.True(RenameRefusal.Follows(RenameRefusal.PrepareRename) && !RenameRefusal.Follows("codeAction/resolve"), "follows F2 only");
    }
}
