namespace FlickGit.App.CommandLine;

/// <summary>
/// A verb the running host has no mechanism for.
///
/// Not an error the user made and not a bug: a fact about which build they are on. A host raises it
/// for a verb it has not implemented, and <see cref="VerbRunner"/> maps it to
/// <c>ExitCodes.ConfigurationError</c> — beside <c>GitNotFoundException</c>, which is the same shape
/// of answer, "the thing you asked for is not available here and here is what is missing".
///
/// <b>An exception rather than a message written where the refusal is discovered</b>, and the reason
/// is worth keeping. Ten of the thirteen <see cref="IWindowVerbs"/> members have no
/// <see cref="VerbOutput"/> to report on — they open a window and a window is its own output — so a
/// refusal written straight to the console was lost the moment the same verb arrived over the socket,
/// where the reply is <i>captured</i> for the client rather than printed: exit code with nothing to
/// show for it. Holding the current output in a field would fix that and is exactly what Hard
/// Requirement 3 forbids, because it is what stops these being singletons. Raising it and letting
/// the one place that already knows where output goes report it costs neither.
/// </summary>
public sealed class HostCapabilityException(string verb)
    : Exception($"`flick {verb}` is not available in this build yet.")
{
    /// <summary>The verb, so a caller can name it without parsing the message.</summary>
    public string Verb { get; } = verb;
}
