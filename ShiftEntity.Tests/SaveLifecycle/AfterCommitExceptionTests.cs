using Microsoft.Extensions.DependencyInjection;
using ShiftSoftware.ShiftEntity.EFCore;
using ShiftSoftware.ShiftEntity.Model;
using ShiftSoftware.ShiftEntity.Tests.SaveHandlers;
using Xunit;
using static ShiftSoftware.ShiftEntity.Tests.SaveHandlers.SavePipelineModel;

namespace ShiftSoftware.ShiftEntity.Tests.SaveLifecycle;

/// <summary>
/// What an app builds and reads of an <see cref="AfterCommitException"/>: the exception as the framework throws it, for
/// a test of the app's own <c>catch</c>, and the message of an answer that reports the save as saved.
/// </summary>
public class AfterCommitExceptionTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static readonly ShiftEntityException NotSent =
        new(new Message("Not sent", "The order is saved, but its email could not be sent."));

    // ── The message of the answer ───────────────────────────────────────────────

    [Fact]
    public void The_saved_message_is_the_first_failures_own_when_it_is_a_ShiftEntityException()
    {
        var saved = new AfterCommitException([NotSent, new InvalidOperationException("The other system did not answer.")]);

        // The failure's own object, as the CRUD endpoints have always answered it.
        Assert.Same(NotSent.Message, saved.SavedMessage());
        Assert.Same(NotSent.Message, saved.SavedMessage(includeFailureMessage: true));
    }

    [Fact]
    public void The_saved_message_is_the_general_one_when_the_first_failure_is_any_other_exception()
    {
        // The second failure has words for the user, but the first one decides.
        var saved = new AfterCommitException([new InvalidOperationException("The other system did not answer."), NotSent]);

        var message = saved.SavedMessage();

        Assert.Equal("Saved", message.Title);
        Assert.Equal("The record was saved, but a follow-up step failed.", message.Body);
    }

    [Fact]
    public void Without_the_failures_own_message_the_saved_message_is_the_general_one_whatever_failed()
    {
        var saved = new AfterCommitException([NotSent]);

        var message = saved.SavedMessage(includeFailureMessage: false);

        Assert.Equal("Saved", message.Title);
        Assert.Equal("The record was saved, but a follow-up step failed.", message.Body);
        Assert.Null(message.SubMessages);

        // A new one each time, so an answer that changes its message changes no other answer's.
        Assert.NotSame(message, saved.SavedMessage(includeFailureMessage: false));
    }

    // ── Built by an app ─────────────────────────────────────────────────────────

    [Fact]
    public async Task An_app_builds_the_exception_as_the_framework_throws_it()
    {
        await using var host = SavePipelineHost.Start(services => services
            .AddSaveHandler<AfterCommitTests.FirstFails>()
            .AddSaveHandler<AfterCommitTests.ThirdFails>());

        var thrown = await host.InScopeAsync(async db =>
        {
            db.Add(new Order { Name = "saved" });
            return await Assert.ThrowsAsync<AfterCommitException>(() => db.SaveChangesAsync(Ct));
        });

        var built = new AfterCommitException(thrown.Failures);

        Assert.Equal(2, thrown.Failures.Count);
        Assert.Equal(thrown.Failures, built.Failures);
        Assert.Equal(thrown.Message, built.Message);
        Assert.Same(thrown.InnerException, built.InnerException);
        Assert.Equal(thrown.SavedMessage().Body, built.SavedMessage().Body);
    }

    [Fact]
    public void The_failures_are_kept_as_given_then_and_at_least_one_is_needed()
    {
        var failures = new List<Exception> { NotSent };
        var saved = new AfterCommitException(failures);

        failures.Add(new InvalidOperationException("Added after the exception was built."));

        Assert.Same(NotSent, Assert.Single(saved.Failures));
        Assert.Same(NotSent, saved.InnerException);
        Assert.Contains("a step that runs after the commit failed: Not sent: The order is saved", saved.Message);

        Assert.Throws<ArgumentNullException>(() => new AfterCommitException(null!));
        Assert.Throws<ArgumentException>(() => new AfterCommitException([]));
        Assert.Throws<ArgumentException>(() => new AfterCommitException([NotSent, null!]));
    }
}
