using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using ShiftSoftware.ShiftEntity.Core;
using ShiftSoftware.ShiftEntity.Core.Notifications;
using ShiftSoftware.ShiftEntity.Model.Dtos;
using ShiftSoftware.ShiftEntity.Model.Flags;
using ShiftSoftware.ShiftEntity.Model.HashIds;
using ShiftSoftware.ShiftEntity.Model.Notifications;
using ShiftSoftware.TypeAuth.Core;
using System.Globalization;

namespace ShiftSoftware.ShiftEntity.EFCore.Notifications;

/// <summary>
/// Handles one event, in its own scope: reads the record as committed, works out who should hear, checks who may hear,
/// merges them into one email and sends it. A no at any check means no email for that person and a log line.
/// </summary>
internal sealed class NotificationPipeline
{
    private readonly IServiceProvider services;
    private readonly ShiftDbContext db;
    private readonly NotificationTopicCatalog catalog;
    private readonly NotificationOptions options;
    private readonly NotificationWhereMatcher whereMatcher;
    private readonly IHashIdService hashIds;
    private readonly ILogger<NotificationPipeline> logger;

    public NotificationPipeline(
        IServiceProvider services,
        ShiftDbContext db,
        NotificationTopicCatalog catalog,
        NotificationOptions options,
        NotificationWhereMatcher whereMatcher,
        IHashIdService hashIds,
        ILogger<NotificationPipeline> logger)
    {
        this.services = services;
        this.db = db;
        this.catalog = catalog;
        this.options = options;
        this.whereMatcher = whereMatcher;
        this.hashIds = hashIds;
        this.logger = logger;
    }

    public async Task ProcessAsync<TEntity>(NotificationRaised<TEntity> raised, CancellationToken cancellationToken)
        where TEntity : ShiftEntity<TEntity>, new()
    {
        var culture = CultureInfo.CurrentCulture;
        var uiCulture = CultureInfo.CurrentUICulture;

        CultureInfo.CurrentCulture = raised.Culture;
        CultureInfo.CurrentUICulture = raised.UICulture;

        try
        {
            var record = await db.Set<TEntity>().AsNoTracking().IgnoreQueryFilters()
                .FirstOrDefaultAsync(x => x.ID == raised.RecordID, cancellationToken);

            if (record is null)
            {
                logger.LogWarning("Notification topic {Topic}: {EntityType} {RecordID} no longer exists, so nothing is sent.",
                    raised.Topic.ID, raised.EntityTypeName, raised.RecordID);
                return;
            }

            if (raised.Topic.ForCustomers)
                await SendToCustomersAsync(raised, record, cancellationToken);
            else
                await SendToStaffAsync(raised, record, cancellationToken);
        }
        finally
        {
            CultureInfo.CurrentCulture = culture;
            CultureInfo.CurrentUICulture = uiCulture;
        }
    }

    // ── Customers ───────────────────────────────────────────────────────────────

    // A topic for customers reaches only the customers on the record. Each one gets an email of their own, so that no
    // customer sees another customer's address.
    private async Task SendToCustomersAsync<TEntity>(NotificationRaised<TEntity> raised, TEntity record, CancellationToken cancellationToken)
        where TEntity : ShiftEntity<TEntity>, new()
    {
        var topic = raised.Topic;
        var addresses = new NotificationAddressMerger(address => LogBadAddress(topic, address));

        foreach (var watcher in raised.Watchers.Where(watcher => watcher.Watches(topic.ID)))
        {
            if (watcher.UserID is not null)
                logger.LogWarning("Notification topic {Topic} is for customers, so user watcher {UserID} on {EntityType} {RecordID} is ignored.",
                    topic.ID, watcher.UserID, raised.EntityTypeName, raised.RecordID);
            else if (watcher.CustomerEmail is not null)
                addresses.Add(watcher.CustomerEmail, NotificationAddressPlace.To);
        }

        if (addresses.To.Count == 0)
        {
            logger.LogInformation("Notification topic {Topic}: {EntityType} {RecordID} has no customer with an address, so nothing is sent.",
                topic.ID, raised.EntityTypeName, raised.RecordID);
            return;
        }

        var body = await ComposeAsync(raised, record, cancellationToken);

        if (body is null || SenderOrLog(raised) is not { } sender)
            return;

        foreach (var address in addresses.To)
        {
            try
            {
                await sender.SendAsync(new([address], [], [], body.Subject, body.HtmlBody, body.TextBody), cancellationToken);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                logger.LogError(exception, "Notification topic {Topic}: sending to a customer on {EntityType} {RecordID} failed. There is no retry.",
                    topic.ID, raised.EntityTypeName, raised.RecordID);
            }
        }
    }

    // ── Staff ───────────────────────────────────────────────────────────────────

    private async Task SendToStaffAsync<TEntity>(NotificationRaised<TEntity> raised, TEntity record, CancellationToken cancellationToken)
        where TEntity : ShiftEntity<TEntity>, new()
    {
        var topic = raised.Topic;
        var directory = ResolveDirectory(topic);
        var entries = await ReadMatchingEntriesAsync(raised, record, cancellationToken);
        var merger = new NotificationAddressMerger(address => LogBadAddress(topic, address));

        foreach (var email in await GetAllowedUserEmailsAsync(raised, record, entries, directory, cancellationToken))
            merger.Add(email, NotificationAddressPlace.To);

        await AddAddressesAsync(raised, record, entries, directory, merger, cancellationToken);

        if (merger.To.Count == 0)
        {
            logger.LogInformation("Notification topic {Topic}: nobody is left in To for {EntityType} {RecordID}, so nothing is sent.",
                topic.ID, raised.EntityTypeName, raised.RecordID);
            return;
        }

        var body = await ComposeAsync(raised, record, cancellationToken);

        if (body is null || SenderOrLog(raised) is not { } sender)
            return;

        await sender.SendAsync(new(merger.To, merger.Cc, merger.Bcc, body.Subject, body.HtmlBody, body.TextBody), cancellationToken);
    }

    // The directory can be registered without what it reads. That reaches no users, and addresses still go.
    private INotificationIdentityDirectory? ResolveDirectory(NotificationTopic topic)
    {
        try
        {
            return services.GetService<INotificationIdentityDirectory>();
        }
        catch (Exception exception) when (exception is InvalidOperationException)
        {
            logger.LogError(exception, "Notification topic {Topic}: {Directory} can't be built, so no user or branch tag is reached. Addresses still go.",
                topic.ID, nameof(INotificationIdentityDirectory));
            return null;
        }
    }

    private async Task<List<NotificationSubscribersEntryDTO>> ReadMatchingEntriesAsync<TEntity>(
        NotificationRaised<TEntity> raised, TEntity record, CancellationToken cancellationToken)
        where TEntity : ShiftEntity<TEntity>, new()
    {
        var list = services.GetRequiredService<INotificationSubscribersList>();
        var entries = await list.GetEntriesAsync(cancellationToken);
        var matching = new List<NotificationSubscribersEntryDTO>();

        foreach (var entry in entries)
        {
            if (!catalog.TryGetTopic(entry.Topic, out _))
            {
                logger.LogWarning("The subscribers list has an entry for unknown topic {Topic}. It is ignored.", entry.Topic);
                continue;
            }

            // An entry may leave out where only for users and teams, which means all. Its addresses are refused below.
            if (entry.Topic == raised.Topic.ID && (entry.Where is null || whereMatcher.Matches(entry.Where, record)))
                matching.Add(entry);
        }

        return matching;
    }

    // Any failure here means no user gets this topic, and there is no retry. It says nothing about addresses, which
    // don't depend on any of this, so they still go.
    private async Task<List<string>> GetAllowedUserEmailsAsync<TEntity>(
        NotificationRaised<TEntity> raised,
        TEntity record,
        List<NotificationSubscribersEntryDTO> entries,
        INotificationIdentityDirectory? directory,
        CancellationToken cancellationToken)
        where TEntity : ShiftEntity<TEntity>, new()
    {
        try
        {
            var candidates = await GatherUsersAsync(raised, entries, directory, cancellationToken);

            return await CheckUsersAsync(raised, record, candidates, directory, cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            logger.LogError(exception, "Notification topic {Topic}: the user check failed for {EntityType} {RecordID}, so no user gets it. Addresses still do.",
                raised.Topic.ID, raised.EntityTypeName, raised.RecordID);
            return [];
        }
    }

    // The users named by the entries, the members of the entries' teams, and the user watchers, minus those left out.
    private async Task<HashSet<long>> GatherUsersAsync<TEntity>(
        NotificationRaised<TEntity> raised,
        List<NotificationSubscribersEntryDTO> entries,
        INotificationIdentityDirectory? directory,
        CancellationToken cancellationToken)
        where TEntity : ShiftEntity<TEntity>, new()
    {
        var topic = raised.Topic;
        var candidates = new HashSet<long>();

        foreach (var watcher in raised.Watchers.Where(watcher => watcher.Watches(topic.ID)))
        {
            if (watcher.UserID is { } userID)
                candidates.Add(userID);
            else if (watcher.CustomerEmail is not null)
                logger.LogWarning("Notification topic {Topic} isn't for customers, so a customer watcher on {EntityType} {RecordID} is ignored.",
                    topic.ID, raised.EntityTypeName, raised.RecordID);
        }

        foreach (var entry in entries)
        {
            foreach (var hash in entry.Users)
            {
                if (hashIds.TryDecode(hash, NotificationIdentityHashIds.User, out var userID))
                    candidates.Add(userID);
                else
                    logger.LogWarning("Notification topic {Topic}: the list holds user '{Hash}', which isn't a valid hash ID. It is ignored.", topic.ID, hash);
            }

            foreach (var hash in entry.Teams)
            {
                if (!hashIds.TryDecode(hash, NotificationIdentityHashIds.Team, out var teamID))
                {
                    logger.LogWarning("Notification topic {Topic}: the list holds team '{Hash}', which isn't a valid hash ID. It is ignored.", topic.ID, hash);
                    continue;
                }

                if (directory is null)
                {
                    logger.LogError("Notification topic {Topic} has team {TeamID}, but no {Directory} is registered, so its members get nothing.",
                        topic.ID, teamID, nameof(INotificationIdentityDirectory));
                    continue;
                }

                var members = await directory.GetTeamMembersAsync(teamID, cancellationToken);

                if (members is null)
                    logger.LogWarning("Notification topic {Topic}: the members of team {TeamID} aren't known, so they get nothing.", topic.ID, teamID);
                else
                    candidates.UnionWith(members);
            }
        }

        candidates.ExceptWith(raised.Except);

        return candidates;
    }

    // The check for users: could they sign in, can they open the screen, can they open this record, is their email
    // verified. Being on a list adds nothing to this.
    private async Task<List<string>> CheckUsersAsync<TEntity>(
        NotificationRaised<TEntity> raised,
        TEntity record,
        HashSet<long> candidates,
        INotificationIdentityDirectory? directory,
        CancellationToken cancellationToken)
        where TEntity : ShiftEntity<TEntity>, new()
    {
        var topic = raised.Topic;
        var allowed = new List<string>();

        if (candidates.Count == 0)
            return allowed;

        if (directory is null)
        {
            logger.LogError("Notification topic {Topic} has {Count} user(s) to check, but no {Directory} is registered, so no user gets it.",
                topic.ID, candidates.Count, nameof(INotificationIdentityDirectory));
            return allowed;
        }

        var lookup = await directory.LookUpUsersAsync(candidates, cancellationToken);

        foreach (var (userID, reason) in lookup.NotEligible)
            logger.LogInformation("Notification topic {Topic}: user {UserID} can't sign in ({Reason}), so they get nothing.", topic.ID, userID, reason);

        foreach (var (userID, reason) in lookup.Unresolved)
            logger.LogWarning("Notification topic {Topic}: the access of user {UserID} can't be worked out ({Reason}), so they get nothing. There is no retry.", topic.ID, userID, reason);

        if (lookup.Eligible.Count == 0)
            return allowed;

        // A repository that can't answer for another user, such as one with an enabled global filter, refuses by throwing.
        // That answer is the same for every user, and the caller then sends to no user.
        var repository = ResolveRepository<TEntity>();

        foreach (var user in lookup.Eligible)
        {
            if (!user.TypeAuth.Can(topic.ReadPermission!, Access.Read))
            {
                logger.LogInformation("Notification topic {Topic}: user {UserID} doesn't have the read permission, so they get nothing.", topic.ID, user.UserID);
                continue;
            }

            if (!repository.CanAccessAs(record, Access.Read, user.TypeAuth, user.Claims))
            {
                logger.LogInformation("Notification topic {Topic}: user {UserID} can't open {EntityType} {RecordID}, so they get nothing.",
                    topic.ID, user.UserID, raised.EntityTypeName, raised.RecordID);
                continue;
            }

            if (string.IsNullOrWhiteSpace(user.Email))
            {
                logger.LogInformation("Notification topic {Topic}: user {UserID} has no email address, so they get nothing.", topic.ID, user.UserID);
                continue;
            }

            if (options.RequireVerifiedEmail && !user.EmailVerified)
            {
                logger.LogInformation("Notification topic {Topic}: the email of user {UserID} isn't verified, so they get nothing.", topic.ID, user.UserID);
                continue;
            }

            allowed.Add(user.Email);
        }

        return allowed;
    }

    // Two repositories for one type can have different rules, and one of them may be open to everyone. The check won't
    // guess between them, because the wrong guess would email a record to someone who can't open it.
    private IShiftEntityFind<TEntity> ResolveRepository<TEntity>()
        where TEntity : ShiftEntity<TEntity>, new()
    {
        if (options.Repositories.TryGetValue(typeof(TEntity), out var named))
            return (IShiftEntityFind<TEntity>)services.GetRequiredService(named);

        var registered = services.GetServices<IShiftEntityFind<TEntity>>().DistinctBy(repository => repository.GetType()).ToList();

        return registered.Count switch
        {
            1 => registered[0],
            0 => throw new InvalidOperationException(
                $"No IShiftEntityFind<{typeof(TEntity).Name}> is registered. RegisterShiftRepositories adds one for each repository class, or name one with UseRepository."),
            _ => throw new InvalidOperationException(
                $"{typeof(TEntity).Name} has {registered.Count} repositories, and their rules can differ. Name the one to check with UseRepository."),
        };
    }

    // The check for addresses: the topic allows them, and the entry has a where that matches the record. The entries here
    // already passed the match, so what is left is the topic's flag and the entry having a where at all.
    private async Task AddAddressesAsync<TEntity>(
        NotificationRaised<TEntity> raised,
        TEntity record,
        List<NotificationSubscribersEntryDTO> entries,
        INotificationIdentityDirectory? directory,
        NotificationAddressMerger merger,
        CancellationToken cancellationToken)
        where TEntity : ShiftEntity<TEntity>, new()
    {
        var topic = raised.Topic;
        IReadOnlyList<TaggedTextDTO>? branchEmails = null;
        var branchEmailsRead = false;

        foreach (var entry in entries)
        {
            if (entry.Emails?.HasAny != true && entry.BranchEmails?.HasAny != true)
                continue;

            if (!topic.AllowEmails)
            {
                logger.LogWarning("Notification topic {Topic} doesn't allow emails, so the addresses on a list entry are ignored.", topic.ID);
                continue;
            }

            if (entry.Where is null)
            {
                logger.LogWarning("Notification topic {Topic}: a list entry has addresses but no where, so they are ignored.", topic.ID);
                continue;
            }

            if (entry.Emails is { } emails)
            {
                merger.AddAll(emails.To, NotificationAddressPlace.To);
                merger.AddAll(emails.Cc, NotificationAddressPlace.Cc);
                merger.AddAll(emails.Bcc, NotificationAddressPlace.Bcc);
            }

            if (entry.BranchEmails is { HasAny: true } tags)
            {
                if (!branchEmailsRead)
                {
                    branchEmails = await ReadBranchEmailsAsync(raised, record, directory, cancellationToken);
                    branchEmailsRead = true;
                }

                if (branchEmails is not null)
                {
                    merger.AddAll(WithTags(branchEmails, tags.To, topic), NotificationAddressPlace.To);
                    merger.AddAll(WithTags(branchEmails, tags.Cc, topic), NotificationAddressPlace.Cc);
                    merger.AddAll(WithTags(branchEmails, tags.Bcc, topic), NotificationAddressPlace.Bcc);
                }
            }
        }
    }

    // Always the record's own branch. A topic can't name another branch.
    private async Task<IReadOnlyList<TaggedTextDTO>?> ReadBranchEmailsAsync<TEntity>(
        NotificationRaised<TEntity> raised,
        TEntity record,
        INotificationIdentityDirectory? directory,
        CancellationToken cancellationToken)
        where TEntity : ShiftEntity<TEntity>, new()
    {
        var topic = raised.Topic;

        if ((record as IEntityHasCompanyBranch<TEntity>)?.CompanyBranchID is not { } branchID)
        {
            logger.LogWarning("Notification topic {Topic}: {EntityType} {RecordID} has no branch, so its branch emails are skipped.",
                topic.ID, raised.EntityTypeName, raised.RecordID);
            return null;
        }

        if (directory is null)
        {
            logger.LogError("Notification topic {Topic} has branch emails, but no {Directory} is registered, so they are skipped.",
                topic.ID, nameof(INotificationIdentityDirectory));
            return null;
        }

        try
        {
            var emails = await directory.GetBranchEmailsAsync(branchID, cancellationToken);

            if (emails is null)
                logger.LogWarning("Notification topic {Topic}: branch {BranchID} has no document, so its branch emails are skipped.", topic.ID, branchID);

            return emails;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            logger.LogError(exception, "Notification topic {Topic}: reading branch {BranchID} failed, so its branch emails are skipped. The other addresses still go.", topic.ID, branchID);
            return null;
        }
    }

    private IEnumerable<string> WithTags(IReadOnlyList<TaggedTextDTO> branchEmails, List<string> tags, NotificationTopic topic)
    {
        foreach (var tag in tags.Where(tag => !string.IsNullOrWhiteSpace(tag)))
        {
            var addresses = branchEmails
                .Where(email => string.Equals(email.Tag?.Trim(), tag.Trim(), StringComparison.OrdinalIgnoreCase))
                .Select(email => email.Text)
                .ToList();

            if (addresses.Count == 0)
                logger.LogInformation("Notification topic {Topic}: the record's branch has no address with tag '{Tag}'.", topic.ID, tag);

            foreach (var address in addresses)
                yield return address;
        }
    }

    // ── Composing ───────────────────────────────────────────────────────────────

    private async Task<NotificationEmailBody?> ComposeAsync<TEntity>(NotificationRaised<TEntity> raised, TEntity record, CancellationToken cancellationToken)
        where TEntity : ShiftEntity<TEntity>, new()
    {
        var topic = raised.Topic;
        var builder = services.GetService<INotificationEmailContent<TEntity>>();

        if (builder is null)
        {
            logger.LogError("Notification topic {Topic}: no INotificationEmailContent<{EntityType}> is registered, so nothing is sent.",
                topic.ID, raised.EntityTypeName);
            return null;
        }

        var content = await builder.BuildAsync(topic, record, cancellationToken);

        if (content is null)
        {
            logger.LogInformation("Notification topic {Topic}: the host gave no email content for {EntityType} {RecordID}, so nothing is sent.",
                topic.ID, raised.EntityTypeName, raised.RecordID);
            return null;
        }

        return NotificationEmailLayout.Compose(content, options.Email);
    }

    private INotificationEmailSender? SenderOrLog(NotificationRaised raised)
    {
        var sender = services.GetService<INotificationEmailSender>();

        if (sender is null)
            logger.LogError("Notification topic {Topic}: no {Sender} is registered, so nothing is sent.", raised.Topic.ID, nameof(INotificationEmailSender));

        return sender;
    }

    // The address itself stays out of the log, because it is personal data.
    private void LogBadAddress(NotificationTopic topic, string address)
        => logger.LogWarning("Notification topic {Topic}: an address of length {Length} isn't valid, so it is ignored.", topic.ID, address.Length);
}
