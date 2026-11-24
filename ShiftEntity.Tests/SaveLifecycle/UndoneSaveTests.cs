using System.Security.Claims;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using ShiftSoftware.ShiftEntity.Core;
using ShiftSoftware.ShiftEntity.Core.Tagging;
using ShiftSoftware.ShiftEntity.EFCore;
using ShiftSoftware.ShiftEntity.EFCore.Tagging;
using ShiftSoftware.ShiftEntity.Model;
using ShiftSoftware.ShiftEntity.Model.Dtos;
using ShiftSoftware.ShiftEntity.Tests.DataLevelAccess.Scenario;
using ShiftSoftware.ShiftEntity.Tests.SaveHandlers;
using ShiftSoftware.ShiftEntity.Web.Services;
using Xunit;
using static ShiftSoftware.ShiftEntity.Tests.SaveHandlers.SavePipelineModel;
using static ShiftSoftware.ShiftEntity.Tests.SaveLifecycle.ScriptedHandlers;

namespace ShiftSoftware.ShiftEntity.Tests.SaveLifecycle;

/// <summary>
/// After a save is rolled back once EF has accepted its rows, the context still shows those rows as saved. It
/// remembers one of them and refuses every later save while that row is tracked. These pin which row it remembers (a
/// row of the app's own type before the join row of a many-to-many), that clearing the tracker always ends the
/// refusal, also when the remembered row is a join row, what a repository's find and save of a taggable record do
/// around it, and that the refusal stays strict when the remembered row is added again.
/// </summary>
public class UndoneSaveTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    // ── The models ──────────────────────────────────────────────────────────────

    /// <summary>A post and a label with an implicit many-to-many between them: its join rows are shared-type rows.</summary>
    public static class JoinModel
    {
        public class Post : ShiftEntity<Post>
        {
            public string? Name { get; set; }
            public List<Label> Labels { get; set; } = [];
        }

        public class Label : ShiftEntity<Label>
        {
            public string? Name { get; set; }
            public List<Post> Posts { get; set; } = [];
        }

        public class Db(DbContextOptions<Db> options) : ShiftDbContext(options)
        {
            public DbSet<Post> Posts => Set<Post>();
            public DbSet<Label> Labels => Set<Label>();
        }
    }

    /// <summary>A record that takes part in the framework's tagging, whose tags are join rows to the tag table.</summary>
    public static class TaggedModel
    {
        public class Article : ShiftEntity<Article>, IShiftEntityTaggable
        {
            public string? Title { get; set; }
            public ICollection<Tag> Tags { get; set; } = new List<Tag>();
        }

        public class Db(DbContextOptions<Db> options) : ShiftDbContext(options)
        {
            public DbSet<Article> Articles => Set<Article>();
        }

        public sealed class ArticleDto : ShiftEntityViewAndUpsertDTO
        {
            public override string? ID { get; set; }
            public string? Title { get; set; }
        }

        public sealed class ArticleMapper : IShiftEntityMapper<Article, ArticleDto, ArticleDto>
        {
            public ArticleDto MapToView(Article entity, MappingContext context = default) => new ArticleDto { Title = entity.Title }.MapBaseFields(entity);

            public Article MapToEntity(ArticleDto dto, Article existing, MappingContext context = default)
            {
                existing.Title = dto.Title;
                return existing;
            }

            public IQueryable<ArticleDto> MapToList(IQueryable<Article> query, MappingContext context = default) =>
                query.Select(article => new ArticleDto { ID = article.ID.ToString() });

            public void CopyEntity(Article source, Article target, MappingContext context = default)
            {
                target.Title = source.Title;
                target.Tags = source.Tags;
            }
        }

        public static ShiftRepository<Db, Article, ArticleDto, ArticleDto> Articles(Db db) => new(db, options => options.UseMapper(new ArticleMapper()));
    }

    /// <summary>Makes the next after-save stage that gets rows fail, once.</summary>
    public sealed class FailNext
    {
        public bool Armed { get; set; }

        public void Fire(string stage)
        {
            if (!Armed)
                return;

            Armed = false;
            throw new InvalidOperationException($"The {stage} handler failed.");
        }
    }

    /// <summary>An after-save handler on every row, the join rows included, which fails when it is armed.</summary>
    public sealed class FailsAfterSaveOfAnyRow(FailNext fail) : IAfterSaveHandler<object>
    {
        public ValueTask AfterSaveAsync(SavedRows<object> rows, CancellationToken cancellationToken)
        {
            fail.Fire("after-save");
            return ValueTask.CompletedTask;
        }
    }

    /// <summary>An after-save handler on articles, which fails when it is armed.</summary>
    public sealed class FailsAfterSaveOfAnArticle(FailNext fail) : IAfterSaveHandler<TaggedModel.Article>
    {
        public ValueTask AfterSaveAsync(SavedRows<TaggedModel.Article> rows, CancellationToken cancellationToken)
        {
            fail.Fire("after-save");
            return ValueTask.CompletedTask;
        }
    }

    // A host on one in-memory SQLite connection, with what a repository needs and a switch to make a save fail.
    private sealed class Host<TDb> : IAsyncDisposable where TDb : ShiftDbContext
    {
        private readonly SqliteConnection connection = new("Data Source=:memory:");

        public Host(Action<IServiceCollection> services)
        {
            connection.Open();

            var collection = new ServiceCollection();
            collection.AddDbContext<TDb>(options => options.UseSqlite(connection));
            collection.AddSingleton<FailNext>();
            collection.AddScoped<ICurrentUserProvider>(_ => FakeCurrentUserProvider.WithClaims((ClaimTypes.NameIdentifier, "42")));
            collection.AddScoped<IdentityClaimProvider>();
            collection.AddSingleton<IHashIdService>(new HashIdService(Options.Create(new ShiftEntityOptions())));
            collection.AddSingleton<IDefaultDataLevelAccess>(new RecordingDefaultDataLevelAccess());
            services(collection);

            Provider = collection.BuildServiceProvider(validateScopes: true);

            using var scope = Provider.CreateScope();
            scope.ServiceProvider.GetRequiredService<TDb>().Database.EnsureCreated();
        }

        public ServiceProvider Provider { get; }

        public FailNext Fail => Provider.GetRequiredService<FailNext>();

        public async Task InScopeAsync(Func<TDb, Task> work)
        {
            await using var scope = Provider.CreateAsyncScope();
            await work(scope.ServiceProvider.GetRequiredService<TDb>());
        }

        public async Task<T> InScopeAsync<T>(Func<TDb, Task<T>> work)
        {
            await using var scope = Provider.CreateAsyncScope();
            return await work(scope.ServiceProvider.GetRequiredService<TDb>());
        }

        public async ValueTask DisposeAsync()
        {
            await Provider.DisposeAsync();
            await connection.DisposeAsync();
        }
    }

    private static async Task<Exception?> RecordAsync(Func<Task> work)
    {
        try
        {
            await work();
            return null;
        }
        catch (Exception exception)
        {
            return exception;
        }
    }

    // ── A join row as the only row of the save ──────────────────────────────────

    [Fact]
    public async Task After_an_undone_save_that_wrote_only_a_join_row_ChangeTracker_Clear_lets_the_context_save_again()
    {
        await using var host = new Host<JoinModel.Db>(services => services.AddSaveHandler<FailsAfterSaveOfAnyRow>());

        var (postId, labelId) = await host.InScopeAsync(async db =>
        {
            var post = new JoinModel.Post { Name = "post" };
            var label = new JoinModel.Label { Name = "label" };
            db.AddRange(post, label);
            await db.SaveChangesAsync(Ct);
            return (post.ID, label.ID);
        });

        await host.InScopeAsync(async db =>
        {
            var post = await db.Posts.Include(p => p.Labels).SingleAsync(p => p.ID == postId, Ct);
            post.Labels.Add(await db.Labels.SingleAsync(l => l.ID == labelId, Ct));

            // Only the join row is written. The handler fails after the write, and the save's transaction rolls back.
            host.Fail.Armed = true;
            var first = await RecordAsync(() => db.SaveChangesAsync(Ct));
            Assert.Equal("The after-save handler failed.", first?.Message);

            // The context remembers the join row: EF accepted it, the database does not have it.
            var refused = Assert.IsType<InvalidOperationException>(await RecordAsync(() => db.SaveChangesAsync(Ct)));
            Assert.Contains("holds rows of a save that was rolled back, a row of LabelPost (Dictionary<string, object>) among them", refused.Message);

            // What the refusal says to do ends it, for a join row too: the next save and the one after both work.
            db.ChangeTracker.Clear();

            db.Labels.Add(new JoinModel.Label { Name = "another" });
            Assert.Null(await RecordAsync(() => db.SaveChangesAsync(Ct)));
            Assert.Null(await RecordAsync(() => db.SaveChangesAsync(Ct)));
        });

        Assert.Empty(await host.InScopeAsync(db => db.Posts.Where(p => p.ID == postId).SelectMany(p => p.Labels).ToListAsync(Ct)));
        Assert.Equal(2, await host.InScopeAsync(db => db.Labels.CountAsync(Ct)));
    }

    // ── A taggable record through a repository ──────────────────────────────────

    // Two tags and an article that has the first of them.
    private static async Task<(long Article, long First, long Second)> SeedAsync(Host<TaggedModel.Db> host) => await host.InScopeAsync(async db =>
    {
        var first = new Tag { Name = "first" };
        var second = new Tag { Name = "second" };
        var article = new TaggedModel.Article { Title = "draft", Tags = [first] };

        db.AddRange(first, second, article);
        await db.SaveChangesAsync(Ct);

        return (article.ID, first.ID, second.ID);
    });

    private static Task<List<long>> TagsInTheDatabaseAsync(Host<TaggedModel.Db> host, long article) => host.InScopeAsync(db =>
        db.Articles.Where(a => a.ID == article).SelectMany(a => a.Tags).Select(t => t.ID).OrderBy(id => id).ToListAsync(Ct));

    private static Task<int> JoinRowsAsync(Host<TaggedModel.Db> host) => host.InScopeAsync(db =>
        db.Set<Dictionary<string, object>>("ArticleTags").CountAsync(Ct));

    private static Host<TaggedModel.Db> TaggedHost(Action<IServiceCollection>? more = null) => new(services =>
    {
        services.AddShiftTagging<TaggedModel.Db>();
        more?.Invoke(services);
    });

    [Fact]
    public async Task A_repository_find_and_save_of_a_taggable_record_round_trips_its_tags_as_before()
    {
        // The normal path, in a host where something acts after the write, so that the context takes note of what each
        // save writes: nothing of it shows while no save has been undone.
        await using var host = TaggedHost(services => services.AddSaveHandler<FailsAfterSaveOfAnArticle>());
        var (id, first, second) = await SeedAsync(host);

        await host.InScopeAsync(async db =>
        {
            var repository = TaggedModel.Articles(db);

            // The repository's find includes the tags of a taggable record.
            var article = (await repository.FindAsync(id, bypass: RepositoryBypass.All))!;
            Assert.Equal([first], article.Tags.Select(t => t.ID));

            article.Title = "edited";
            article.Tags.Remove(article.Tags.Single());
            article.Tags.Add(await db.Tags.SingleAsync(t => t.ID == second, Ct));
            await repository.SaveChangesAsync();

            // The repository's reload after the save, and a second find, show what was saved.
            Assert.Equal([second], article.Tags.Select(t => t.ID));
            Assert.Equal([second], (await repository.FindAsync(id, bypass: RepositoryBypass.All))!.Tags.Select(t => t.ID));

            // And the context saves again.
            article.Title = "edited again";
            await repository.SaveChangesAsync();
        });

        Assert.Equal([second], await TagsInTheDatabaseAsync(host, id));
        Assert.Equal(1, await JoinRowsAsync(host));
        Assert.Equal("edited again", await host.InScopeAsync(db => db.Articles.Select(a => a.Title).SingleAsync(Ct)));
    }

    [Fact]
    public async Task An_undone_repository_save_of_a_taggable_record_names_the_record_and_after_Clear_its_find_and_save_work()
    {
        await using var host = TaggedHost(services => services.AddSaveHandler<FailsAfterSaveOfAnArticle>());
        var (id, first, second) = await SeedAsync(host);

        await host.InScopeAsync(async db =>
        {
            var repository = TaggedModel.Articles(db);
            var article = (await repository.FindAsync(id, bypass: RepositoryBypass.All))!;

            // A value and the tags change. The save writes the article and its join rows, and fails after the write.
            article.Title = "edited";
            article.Tags.Add(await db.Tags.SingleAsync(t => t.ID == second, Ct));

            host.Fail.Armed = true;
            Assert.Equal("The after-save handler failed.", (await RecordAsync(() => repository.SaveChangesAsync()))?.Message);

            // The context remembers the article, a row of the app's own type, and not one of the join rows.
            var refused = Assert.IsType<InvalidOperationException>(await RecordAsync(() => repository.SaveChangesAsync()));
            Assert.Contains("a row of Article among them", refused.Message);

            // Cleared, the repository's find shows what the database has, and a save works.
            db.ChangeTracker.Clear();

            var found = (await repository.FindAsync(id, bypass: RepositoryBypass.All))!;
            Assert.NotSame(article, found);
            Assert.Equal("draft", found.Title);
            Assert.Equal([first], found.Tags.Select(t => t.ID));

            found.Title = "edited";
            found.Tags.Add(await db.Tags.SingleAsync(t => t.ID == second, Ct));
            await repository.SaveChangesAsync();
        });

        Assert.Equal([first, second], await TagsInTheDatabaseAsync(host, id));
        Assert.Equal(2, await JoinRowsAsync(host));
        Assert.Equal("edited", await host.InScopeAsync(db => db.Articles.Select(a => a.Title).SingleAsync(Ct)));
    }

    [Fact]
    public async Task Without_Clear_the_find_after_an_undone_save_returns_the_tracked_record_with_the_rolled_back_tag_and_a_save_is_refused()
    {
        await using var host = TaggedHost(services => services.AddSaveHandler<FailsAfterSaveOfAnArticle>());
        var (id, first, second) = await SeedAsync(host);

        await host.InScopeAsync(async db =>
        {
            var repository = TaggedModel.Articles(db);
            var article = (await repository.FindAsync(id, bypass: RepositoryBypass.All))!;

            article.Title = "edited";
            article.Tags.Add(await db.Tags.SingleAsync(t => t.ID == second, Ct));

            host.Fail.Armed = true;
            Assert.NotNull(await RecordAsync(() => repository.SaveChangesAsync()));

            // The find is a query, which the refusal does not stop. It returns the tracked instance, and EF keeps the join
            // row it accepted: the record still shows the tag the database does not have.
            var found = (await repository.FindAsync(id, bypass: RepositoryBypass.All))!;
            Assert.Same(article, found);
            Assert.Equal([first, second], found.Tags.Select(t => t.ID).OrderBy(t => t));

            // So saving again would write nothing for it and report no error. The context refuses instead.
            found.Title = "edited again";
            var refused = Assert.IsType<InvalidOperationException>(await RecordAsync(() => repository.SaveChangesAsync()));
            Assert.Contains("a row of Article among them", refused.Message);
        });

        Assert.Equal([first], await TagsInTheDatabaseAsync(host, id));
        Assert.Equal("draft", await host.InScopeAsync(db => db.Articles.Select(a => a.Title).SingleAsync(Ct)));
    }

    [Fact]
    public async Task An_undone_save_that_changed_only_tags_through_the_context_is_refused_until_the_tracker_is_cleared()
    {
        await using var host = TaggedHost(services => services.AddSaveHandler<FailsAfterSaveOfAnyRow>());
        var (id, first, second) = await SeedAsync(host);

        await host.InScopeAsync(async db =>
        {
            // A save made outside a repository that changes only tags writes only join rows.
            var article = await db.Articles.Include(a => a.Tags).SingleAsync(a => a.ID == id, Ct);
            article.Tags.Add(await db.Tags.SingleAsync(t => t.ID == second, Ct));

            host.Fail.Armed = true;
            Assert.NotNull(await RecordAsync(() => db.SaveChangesAsync(Ct)));

            // The join row is all the save wrote, so it is the row the context remembers, and the next save is refused.
            var refused = Assert.IsType<InvalidOperationException>(await RecordAsync(() => db.SaveChangesAsync(Ct)));
            Assert.Contains("a row of ArticleTags (Dictionary<string, object>) among them", refused.Message);

            // Cleared, it saves.
            db.ChangeTracker.Clear();

            var again = await db.Articles.Include(a => a.Tags).SingleAsync(a => a.ID == id, Ct);
            Assert.Equal([first], again.Tags.Select(t => t.ID));

            again.Tags.Add(await db.Tags.SingleAsync(t => t.ID == second, Ct));
            await db.SaveChangesAsync(Ct);
        });

        Assert.Equal([first, second], await TagsInTheDatabaseAsync(host, id));
        Assert.Equal(2, await JoinRowsAsync(host));
    }

    // ── The refusal stays strict ────────────────────────────────────────────────

    [Fact]
    public async Task Adding_the_remembered_row_again_does_not_end_the_refusal()
    {
        // The context remembers one row of the undone save. Adding that instance again (EF's Add changes that one entry
        // alone) could leave the other rows of the save tracked as saved, and a save would then skip them without an
        // error. So the refusal holds until the row is no longer tracked.
        await using var host = SavePipelineHost.Start(services => services.AddScript(new Script<Order>
        {
            AfterSave = (rows, _) => rows.Any(row => row.Entity.Name == "fails") ? throw new InvalidOperationException("The handler failed.") : Task.CompletedTask,
        }));

        await host.InScopeAsync(async db =>
        {
            var order = new Order { Name = "fails", Lines = [new OrderLine { Note = "a" }, new OrderLine { Note = "b" }] };
            db.Add(order);
            await Assert.ThrowsAsync<InvalidOperationException>(() => db.SaveChangesAsync(Ct));

            // The retry re-adds the order only. Its lines stay tracked as saved, which the database does not have.
            order.Name = "fixed";
            db.Add(order);
            Assert.Equal(EntityState.Added, db.Entry(order).State);
            Assert.All(order.Lines, line => Assert.Equal(EntityState.Unchanged, db.Entry(line).State));

            var refused = await Assert.ThrowsAsync<InvalidOperationException>(() => db.SaveChangesAsync(Ct));
            Assert.Contains("a row of Order among them", refused.Message);
            Assert.Contains("Adding or changing the same instances again does not end this", refused.Message);
            Assert.Contains("a new context, or from a cleared change tracker, on each attempt", refused.Message);

            // Marked modified, the same.
            db.Entry(order).State = EntityState.Modified;
            await Assert.ThrowsAsync<InvalidOperationException>(() => db.SaveChangesAsync(Ct));
        });

        // A retry on a new context saves the order and every line.
        await host.InScopeAsync(async db =>
        {
            db.Add(new Order { Name = "fixed", Lines = [new OrderLine { Note = "a" }, new OrderLine { Note = "b" }] });
            await db.SaveChangesAsync(Ct);
        });

        Assert.Equal(1, await host.InScopeAsync(db => db.Orders.CountAsync(Ct)));
        Assert.Equal(2, await host.InScopeAsync(db => db.Lines.CountAsync(Ct)));
    }
}
