using ShiftSoftware.ShiftEntity.Model;

namespace ShiftSoftware.ShiftEntity.EFCore;

/// <summary>
/// The rows were committed, and one or more steps that run after the commit failed. Nothing was undone, and every
/// other step still ran. It is thrown from the call that committed: <c>SaveChanges</c> for a save on its own, and
/// <c>Commit</c> for a transaction the app began.
/// </summary>
/// <remarks>
/// A caller that turns exceptions around a save into an error must treat this one as a success with a message: the
/// data is saved. <see cref="SavedMessage"/> is that message, and an answer carries it with the marker
/// <see cref="ShiftEntityResponse.SavedWithFailure"/>, as the framework's CRUD endpoints do.
/// </remarks>
public sealed class AfterCommitException : Exception
{
    /// <summary>
    /// The exception the framework throws when steps after a commit failed. For a test of code that catches it, such
    /// as an endpoint whose stand-in for the save throws this.
    /// </summary>
    /// <param name="failures">What each failed step threw, in the order the steps ran. At least one.</param>
    public AfterCommitException(IReadOnlyList<Exception> failures)
        : this(Copy(failures))
    {
    }

    private AfterCommitException(Exception[] failures)
        : base(Describe(failures), failures[0])
    {
        Failures = Array.AsReadOnly(failures);
    }

    /// <summary>What each failed step threw, in the order the steps ran.</summary>
    public IReadOnlyList<Exception> Failures { get; }

    /// <summary>
    /// The message of an answer that reports the save as saved: the first failure's own message when that failure is a
    /// <see cref="ShiftEntityException"/>, which says what failed in words meant for the user, and a general one
    /// otherwise, "Saved" with "The record was saved, but a follow-up step failed." Any other exception's text is not
    /// meant for a user, so it is never the message.
    /// </summary>
    /// <param name="includeFailureMessage">
    /// False gives the general message whatever failed: for a caller who must not see what the app's own steps report,
    /// such as an anonymous one.
    /// </param>
    /// <returns>The failure's own message object, or a new general one.</returns>
    public Message SavedMessage(bool includeFailureMessage = true) =>
        includeFailureMessage && Failures[0] is ShiftEntityException { Message: { } message }
            ? message
            : new Message("Saved", "The record was saved, but a follow-up step failed.");

    private static Exception[] Copy(IReadOnlyList<Exception> failures)
    {
        ArgumentNullException.ThrowIfNull(failures);

        if (failures.Count == 0)
            throw new ArgumentException("An AfterCommitException holds at least one failure.", nameof(failures));

        var copy = new Exception[failures.Count];

        for (var i = 0; i < copy.Length; i++)
            copy[i] = failures[i] ?? throw new ArgumentException("A failure is null.", nameof(failures));

        return copy;
    }

    private static string Describe(IReadOnlyList<Exception> failures) => failures.Count == 1
        ? $"The rows were committed, but a step that runs after the commit failed: {Text(failures[0])}"
        : $"The rows were committed, but {failures.Count} steps that run after the commit failed. The first: {Text(failures[0])}";

    // A ShiftEntityException keeps what it has to say in a message of its own.
    private static string Text(Exception failure) => failure is ShiftEntityException { Message: { } message }
        ? string.IsNullOrEmpty(message.Body) ? message.Title : $"{message.Title}: {message.Body}"
        : failure.Message;
}
