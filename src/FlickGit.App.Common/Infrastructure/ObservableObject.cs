using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace FlickGit.App.Infrastructure;

/// <summary>
/// The smallest thing that makes WPF binding work.
///
/// No MVVM toolkit: CLAUDE.md rules out anything "heavy enough to affect startup", and
/// this is a resident process whose whole value proposition is that it has already
/// started. Twenty lines of INotifyPropertyChanged is the entire debt.
/// </summary>
public abstract class ObservableObject : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    protected void Raise([CallerMemberName] string? propertyName = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));

    /// <summary>
    /// Assigns and notifies, returning false when the value did not change.
    ///
    /// The equality check is not an optimisation here. The commit window's file list is
    /// rebuilt on every status refresh, and raising PropertyChanged for a value that did
    /// not change re-triggers bindings that re-render rows — which is visible as a flicker
    /// on a list of forty files.
    /// </summary>
    protected bool Set<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
            return false;

        field = value;
        Raise(propertyName);
        return true;
    }
}
