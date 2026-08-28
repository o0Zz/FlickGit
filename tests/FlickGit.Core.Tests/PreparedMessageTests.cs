using FlickGit.Commits;
using Xunit;

namespace FlickGit.Tests;

/// <summary>
/// `MERGE_MSG` cleanup — CLAUDE.md, Hard Requirement 4, "parsers and the pure functions beside them".
///
/// <see cref="PreparedMessageService.Clean"/> is the half of the prepared-message feature where a
/// wrong answer is expensive: Git writes its own <c># Conflicts:</c> block into this file after a
/// conflicted merge, and the message goes out through <c>git commit -F</c>, which strips nothing. A
/// miss here puts that block in the commit.
/// </summary>
public class PreparedMessageTests
{
    [Fact]
    public void StripsTheConflictsBlockGitAppends()
    {
        string? message = PreparedMessageService.Clean(
            "Merge branch 'feature/storage-gw'\n"
            + "\n"
            + "# Conflicts:\n"
            + "#\tsrc/GatewayClient.cs\n");

        Assert.Equal("Merge branch 'feature/storage-gw'", message);
    }

    [Fact]
    public void KeepsAHashThatIsNotTheFirstCharacterOfItsLine()
    {
        //An issue number is ordinary text. Only a leading # is a comment.
        string? message = PreparedMessageService.Clean("fix: pool leak on reconnect (#412)\n");

        Assert.Equal("fix: pool leak on reconnect (#412)", message);
    }

    [Fact]
    public void AFileWithNoMessageLeftInItIsNull()
    {
        //Null rather than empty, so the caller falls through to the AI instead of putting an empty
        //message in the box.
        Assert.Null(PreparedMessageService.Clean("# nothing but a comment\n\n   \n"));
    }

    [Fact]
    public void NormalisesCrlfAndTrims()
    {
        string? message = PreparedMessageService.Clean("\r\nfeat: add pooling\r\n\r\nWhy it helps.\r\n\r\n");

        Assert.Equal("feat: add pooling\n\nWhy it helps.", message);
    }
}
