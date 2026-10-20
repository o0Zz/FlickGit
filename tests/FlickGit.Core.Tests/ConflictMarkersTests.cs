using FlickGit.Merges;
using Xunit;

namespace FlickGit.Tests;

/// <summary>
/// In scope as a <b>safety rule</b>: saving a conflicted file marks it resolved only when no marker is
/// left, so a save half-way through a resolution can never record the markers as the answer.
/// </summary>
public class ConflictMarkersTests
{
    [Theory]
    //A stash pop's own labels, with CRLF, since that is how the file arrives on Windows.
    [InlineData("a\r\n<<<<<<< Updated upstream\r\nb\r\n=======\r\nc\r\n>>>>>>> Stashed changes\r\n", true)]
    //One block taken out, the separator and the closing line forgotten.
    [InlineData("a\nb\n=======\nc\n>>>>>>> Stashed changes\n", true)]
    //diff3's base marker on its own.
    [InlineData("a\n||||||| Stash base\nb\n", true)]
    //A conflict-marker-size attribute makes the markers longer, not different.
    [InlineData("a\n<<<<<<<<<< HEAD\nb\n", true)]
    [InlineData("a\nb\nc\n", false)]
    //Not a marker: shorter than seven, or the run continues into text.
    [InlineData("<<<<<< six\n==>\na <<<<<<< b\n<<<<<<<x\n", false)]
    [InlineData("", false)]
    public void ReportsWhetherAnyMarkerLineIsLeft(string text, bool remain) =>
        Assert.Equal(remain, ConflictMarkers.Remain(text));
}
