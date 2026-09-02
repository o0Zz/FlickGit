using Avalonia;
using Avalonia.Markup.Xaml;

namespace FlickGit.App.Mac;

/// <summary>
/// The Avalonia application object.
///
/// Deliberately thin, and for the same reason <c>App.xaml.cs</c> on Windows is the composition root
/// and nothing else: everything this application does lives in FlickGit.Core and
/// FlickGit.App.Common, and the job here is to hand those the three platform seams and get out of
/// the way.
/// </summary>
public sealed class App : Application
{
    public override void Initialize() => AvaloniaXamlLoader.Load(this);
}
