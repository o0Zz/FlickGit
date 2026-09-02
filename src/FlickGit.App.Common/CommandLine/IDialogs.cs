namespace FlickGit.App.CommandLine;

/// <summary>
/// The two windows the verb layer is allowed to open: one that states an outcome, and one that asks
/// a yes/no question. See <see cref="INotifier"/> for why these are not the same interface.
///
/// Neither takes an owner. These answer a verb that may have no window behind it at all — run from
/// a context menu or a terminal — so the host decides where to put them.
/// </summary>
public interface IDialogs
{
    /// <summary>
    /// A window, unconditionally, for the cases worth showing even with a console open.
    /// </summary>
    /// <param name="compact">
    /// A one-line outcome rather than a full notice. The fallback shape for
    /// <c>VerbOutput.Say</c> when there is no console and no notification area.
    /// </param>
    void Notice(string title, string message, bool compact);

    /// <summary>
    /// A yes/no question. <paramref name="destructive"/> marks the answer that cannot be undone, so
    /// the host can present it as the dangerous one rather than the default.
    /// </summary>
    bool Confirm(string title, string body, string yes, string no, bool destructive = false);
}
