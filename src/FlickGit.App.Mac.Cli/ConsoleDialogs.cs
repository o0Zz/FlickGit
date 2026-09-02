using FlickGit.App.CommandLine;
using FlickGit.App.Infrastructure;

namespace FlickGit.App.Mac;

/// <summary>
/// <see cref="IDialogs"/> for a host with no windows: a notice is printed, and a question is asked
/// on the terminal.
///
/// <b>A closed stdin answers no.</b> Confirm is only ever reached for something on CLAUDE.md's
/// Safety Rules list or an action marked <c>RequiresConfirmation</c>, so the answer when nobody can
/// be asked — a script, a pipe, a cron job — has to be the one that does not proceed. Defaulting to
/// yes would make an unattended invocation the one surface that skips the confirmation, which is
/// what "the command line is not a shortcut around these rules" forbids.
/// </summary>
public sealed class ConsoleDialogs : IDialogs
{
    public void Notice(string title, string message, bool compact)
    {
        //compact distinguishes a one-line window from a full one. Printed text has no such
        //distinction to make.
        _ = compact;

        ConsoleOutput.WriteLine(title);
        ConsoleOutput.WriteLine(message);
    }

    public Task<bool> ConfirmAsync(string title, string body, string yes, string no, bool destructive = false)
    {
        //destructive changes which button a window paints as dangerous. There are no buttons here,
        //and the prompt already defaults to no.
        _ = destructive;

        ConsoleOutput.WriteLine(title);
        ConsoleOutput.WriteLine(body);

        if (Console.IsInputRedirected)
        {
            //Say why, rather than looking as though it was declined for some other reason.
            ConsoleOutput.WriteError($"{no} — there is no terminal to ask on.");

            return Task.FromResult(false);
        }

        ConsoleOutput.WriteLine($"{yes} / {no}? [y/N]");

        string? answer = Console.ReadLine()?.Trim();

        return Task.FromResult(
            string.Equals(answer, "y", StringComparison.OrdinalIgnoreCase)
            || string.Equals(answer, "yes", StringComparison.OrdinalIgnoreCase));
    }
}
