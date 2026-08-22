using FlickGit.App.Localization;
using FlickGit.Branches;

namespace FlickGit.App.ViewModels;

/// <summary>
/// The words for a <see cref="BranchResolution"/>.
///
/// Split from the resolution itself when that moved to <c>FlickGit.Core</c>: the decision is Git
/// behaviour and belongs where it can be tested, the wording is presentation and belongs where the
/// language file is. <see cref="CommitFlow"/> makes the same split about itself for the same
/// reason.
/// </summary>
internal static class BranchHintText
{
    public static string For(BranchIntent intent) => intent switch
    {
        BranchIntent.Current => Strings.Get("branch.current"),
        BranchIntent.ExistingBranch => Strings.Get("branch.willswitch"),
        BranchIntent.NewBranch => Strings.Get("branch.willcreate"),
        BranchIntent.Invalid => Strings.Get("branch.invalid"),
        _ => string.Empty,
    };
}
