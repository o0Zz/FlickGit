using FlickGit.App.Settings;

namespace FlickGit.App.Mac;

/// <summary>
/// <see cref="IAutostart"/> where there is no mechanism to drive.
///
/// This host is <c>net9.0</c> and therefore runs on Windows too, which is what made the socket
/// transport testable at all — but <c>launchd</c> is not there, and the macOS implementation is
/// fenced off by <c>[SupportedOSPlatform("macos")]</c> so it cannot be constructed. Something still
/// has to satisfy <c>EnvironmentReports</c>, which takes this interface to answer
/// <c>flick autostart</c>.
///
/// It answers rather than throwing, because the interface already has a shape for "could not":
/// a flag and a sentence. <see cref="IsEnabled"/> is false, which is true — nothing is registered.
/// </summary>
public sealed class UnsupportedAutostart : IAutostart
{
    private const string Message = "Starting with the session is only available on macOS in this build.";

    public bool IsEnabled() => false;

    public (bool Succeeded, string Message) Enable() => (false, Message);

    public (bool Succeeded, string Message) Disable() => (false, Message);
}
