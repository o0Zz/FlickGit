using Xunit;

namespace FlickGit.Tests;

/// <summary>
/// For a behaviour that exists only on Windows, as opposed to one whose <i>spelling</i> differs --
/// which is <see cref="PlatformPaths"/>'s job instead.
///
/// There is exactly one such behaviour in scope: the drive-relative path repair in
/// <c>Verb.NormalisePath</c>. <c>C:</c> means "the current directory on drive C" and has no analogue
/// off Windows, where it is an ordinary two-character directory name. Skipping is right where
/// converting would invent a Unix meaning the platform does not have.
/// </summary>
public sealed class WindowsOnlyFactAttribute : FactAttribute
{
    public WindowsOnlyFactAttribute()
    {
        if (!OperatingSystem.IsWindows())
            Skip = "Windows-only behaviour.";
    }
}

/// <inheritdoc cref="WindowsOnlyFactAttribute"/>
public sealed class WindowsOnlyTheoryAttribute : TheoryAttribute
{
    public WindowsOnlyTheoryAttribute()
    {
        if (!OperatingSystem.IsWindows())
            Skip = "Windows-only behaviour.";
    }
}
