using Avalonia;

namespace FlickGit.App.Mac;

/// <summary>
/// The entry point for the binary inside <c>FlickGit.app</c>.
///
/// <b>Not the CLI.</b> <c>flick</c> is its own executable, which answers text verbs itself and
/// forwards the rest over the socket — the same division Windows makes between the stub and the
/// resident process, and for the same reason: the common case is a verb that prints and exits, and
/// it must not pay a UI toolkit's startup to do it.
/// </summary>
internal static class Program
{
    [STAThread]
    private static int Main(string[] arguments) =>
        BuildAvaloniaApp().StartWithClassicDesktopLifetime(arguments);

    /// <summary>Also called by the Avalonia designer, which is why it is public and parameterless.</summary>
    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .LogToTrace();
}
