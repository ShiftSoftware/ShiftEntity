using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using ShiftSoftware.ShiftEntity.Core;
using ShiftSoftware.ShiftEntity.EFCore;
using ShiftSoftware.ShiftEntity.Model;
using ShiftSoftware.ShiftEntity.Model.Dtos;
using ShiftSoftware.ShiftEntity.Web;
using ShiftSoftware.ShiftEntity.Web.Services;
using ShiftSoftware.ShiftEntity.Tests.DataLevelAccess.Scenario;
using ShiftSoftware.ShiftEntity.Tests.SaveHandlers;
using Xunit;
using static ShiftSoftware.ShiftEntity.Tests.SaveHandlers.SavePipelineModel;

namespace ShiftSoftware.ShiftEntity.Tests.SaveLifecycle;

/// <summary>
/// A repository's save is the context's save, told that a repository made it. So the handlers of a row run the same
/// way whoever saves it, and what is the repository's own (the stamp of every row it writes, the unique hash, the
/// reload) sits on the same stages.
/// </summary>
public class RepositorySaveTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private const long User = 42;

    public sealed class VoucherDto : ShiftEntityViewAndUpsertDTO
    {
        public override string? ID { get; set; }
        public string? Name { get; set; }
    }

    public sealed class VoucherMapper : IShiftEntityMapper<Voucher, VoucherDto, VoucherDto>
    {
        public VoucherDto MapToView(Voucher entity, MappingContext context = default) => new VoucherDto { Name = entity.Name }.MapBaseFields(entity);

        public Voucher MapToEntity(VoucherDto dto, Voucher existing, MappingContext context = default)
        {
            existing.Name = dto.Name;
            return existing;
        }

        public IQueryable<VoucherDto> MapToList(IQueryable<Voucher> query, MappingContext context = default) =>
            query.Select(voucher => new VoucherDto { ID = voucher.ID.ToString() });

        public void CopyEntity(Voucher source, Voucher target, MappingContext context = default)
        {
            target.Series = source.Series;
            target.Number = source.Number;
            target.Name = source.Name;
            target.Memo = source.Memo;
        }
    }

    private static ShiftRepository<Db, Voucher, VoucherDto, VoucherDto> Vouchers(Db db, bool withMemo = false) => new(db, options =>
    {
        options.UseMapper(new VoucherMapper());

        if (withMemo)
            options.IncludeRelatedEntitiesWithFindAsync(query => query.Include(voucher => voucher.Memo));
    });

    // What the framework's own registrations give a repository, with a signed-in user.
    private static IServiceCollection ForARepository(IServiceCollection services) => services
        .AddScoped<ICurrentUserProvider>(_ => FakeCurrentUserProvider.WithClaims((ClaimTypes.NameIdentifier, User.ToString())))
        .AddScoped<IdentityClaimProvider>()
        .AddSingleton<IHashIdService>(new HashIdService(Options.Create(new ShiftEntityOptions())))
        .AddSingleton<IDefaultDataLevelAccess>(new RecordingDefaultDataLevelAccess());

    // ── Whoever saves ───────────────────────────────────────────────────────────

    /// <summary>Notes each voucher it is called for, before the save and once it is committed.</summary>
    public sealed class Seen(SaveLog log) : IBeforeSaveHandler<Voucher>, IAfterCommitHandler<Voucher>
    {
        public ValueTask BeforeSaveAsync(PendingRows<Voucher> rows, CancellationToken cancellationToken)
        {
            foreach (var row in rows)
                log.Add($"before save: {row.Change} '{row.Entity.Name}'{(rows.IsRepositorySave ? ", a repository's save" : "")}");

            return ValueTask.CompletedTask;
        }

        public ValueTask AfterCommitAsync(SavedRows<Voucher> rows, CancellationToken cancellationToken)
        {
            foreach (var row in rows)
                log.Add($"after commit: {row.Change} '{row.Entity.Name}'");

            return ValueTask.CompletedTask;
        }
    }

    /// <summary>After a memo that asks for a voucher is saved, saves one: a save made from inside a save.</summary>
    public sealed class VoucherForAMemo : IAfterSaveHandler<Memo>
    {
        public async ValueTask AfterSaveAsync(SavedRows<Memo> rows, CancellationToken cancellationToken)
        {
            if (!rows.Any(row => row.Change == RowChange.Added && row.Entity.Name == "wants a voucher"))
                return;

            rows.Db.Add(new Voucher { Series = "n", Number = 1, Name = "nested" });
            await rows.Db.SaveChangesAsync(cancellationToken);
        }
    }

    [Fact]
    public async Task The_same_handler_runs_however_a_row_is_saved_and_for_nothing_that_goes_around_the_contexts_save()
    {
        await using var host = SavePipelineHost.Start(services => ForARepository(services).AddSaveHandler<Seen>().AddSaveHandler<VoucherForAMemo>());

        // Through a repository.
        await host.InScopeAsync(async db =>
        {
            var repository = Vouchers(db);
            repository.Add(new Voucher { Series = "a", Number = 1, Name = "repository" });
            await repository.SaveChangesAsync();
        });

        // A plain save.
        await host.InScopeAsync(async db =>
        {
            db.Add(new Voucher { Series = "b", Number = 1, Name = "plain" });
            await db.SaveChangesAsync(Ct);
        });

        // The synchronous save, in a scope that a seeder or a job makes for itself.
        using (var scope = host.Provider.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<Db>();
            db.AddRange(new Voucher { Series = "c", Number = 1, Name = "seeded 1" }, new Voucher { Series = "c", Number = 2, Name = "seeded 2" });
            db.SaveChanges();
        }

        // A save that a handler makes from inside another save.
        await host.InScopeAsync(async db =>
        {
            db.Add(new Memo { Name = "wants a voucher" });
            await db.SaveChangesAsync(Ct);
        });

        Assert.Equal(
        [
            "before save: Added 'repository', a repository's save", "after commit: Added 'repository'",
            "before save: Added 'plain'", "after commit: Added 'plain'",
            "before save: Added 'seeded 1'", "before save: Added 'seeded 2'", "after commit: Added 'seeded 1'", "after commit: Added 'seeded 2'",
            "before save: Added 'nested'", "after commit: Added 'nested'",
        ], host.Log.Lines);

        host.Log.Clear();

        // What goes around the context's save is not a save: the bulk statements and raw SQL.
        await host.InScopeAsync(async db =>
        {
            await db.Vouchers.Where(x => x.Name == "plain").ExecuteUpdateAsync(set => set.SetProperty(x => x.Name, "updated in bulk"), Ct);
            await db.Vouchers.Where(x => x.Name == "seeded 1").ExecuteDeleteAsync(Ct);
            await db.Database.ExecuteSqlRawAsync("UPDATE \"Vouchers\" SET \"Name\" = 'written by raw SQL' WHERE \"Name\" = 'repository'", Ct);
        });

        Assert.Empty(host.Log.Lines);

        Assert.Equal(
            ["nested", "seeded 2", "updated in bulk", "written by raw SQL"],
            await host.InScopeAsync(db => db.Vouchers.Select(x => x.Name).OrderBy(x => x).ToListAsync(Ct)));
    }

    // ── The stamp ───────────────────────────────────────────────────────────────

    [Fact]
    public async Task A_repository_save_stamps_every_row_it_writes_whatever_SuppressAuditStamping_says()
    {
        await using var host = SavePipelineHost.Start(services => ForARepository(services));

        // A plain save under the switch fills nothing.
        await host.InScopeAsync(async db =>
        {
            var voucher = new Voucher { Series = "a", Number = 1, Name = "voucher" };
            var memo = new Memo { Name = "memo" };
            db.AddRange(voucher, memo);

            using (db.SuppressAuditStamping())
                await db.SaveChangesAsync(Ct);

            Assert.Equal(default, voucher.CreateDate);
            Assert.Null(memo.CreatedByUserID);
        });

        // A plain save of a modified row leaves its last-save columns alone, with or without the switch.
        await host.InScopeAsync(async db =>
        {
            var memo = await db.Memos.SingleAsync(Ct);
            memo.Name = "memo, changed on the plain context";
            await db.SaveChangesAsync(Ct);

            Assert.Equal(default, memo.LastSaveDate);
        });

        await host.InScopeAsync(async db =>
        {
            var voucher = await db.Vouchers.SingleAsync(Ct);
            var memo = await db.Memos.SingleAsync(Ct);
            var added = new Memo { Name = "added" };

            voucher.Name = "voucher, changed";
            memo.Name = "memo, changed";
            db.Add(added);

            using (db.SuppressAuditStamping())
                await Vouchers(db).SaveChangesAsync();

            // A modified row gets its last-save columns, of the repository's own type and of any other.
            Assert.NotEqual(default, voucher.LastSaveDate);
            Assert.Equal(User, voucher.LastSavedByUserID);
            Assert.Equal(voucher.LastSaveDate, memo.LastSaveDate);
            Assert.Equal(User, memo.LastSavedByUserID);

            // A new row is filled.
            Assert.Equal(voucher.LastSaveDate, added.CreateDate);
            Assert.Equal(User, added.CreatedByUserID);
        });

        // The stamp is written, and not only set on the records.
        await using var bare = host.Bare();
        Assert.All(await bare.Memos.AsNoTracking().Where(x => x.Name != "added").ToListAsync(Ct), memo => Assert.Equal(User, memo.LastSavedByUserID));
        Assert.Equal(User, (await bare.Vouchers.AsNoTracking().SingleAsync(Ct)).LastSavedByUserID);
    }

    [Fact]
    public async Task The_stamp_of_a_modified_row_is_written_when_the_app_has_switched_change_detection_off()
    {
        await using var host = SavePipelineHost.Start(services => ForARepository(services));

        await host.InScopeAsync(async db =>
        {
            db.Add(new Voucher { Series = "a", Number = 1, Name = "voucher" });
            await db.SaveChangesAsync(Ct);
        });

        await host.InScopeAsync(async db =>
        {
            var voucher = await db.Vouchers.SingleAsync(Ct);

            // The app marks its own change. Nothing would find a stamp set as a plain property after that, so the
            // stamper writes through EF's entry, which marks the columns at once.
            db.ChangeTracker.AutoDetectChangesEnabled = false;
            voucher.Name = "changed";
            db.Entry(voucher).Property(x => x.Name).IsModified = true;

            await Vouchers(db).SaveChangesAsync();
        });

        await using var bare = host.Bare();
        var stored = await bare.Vouchers.AsNoTracking().SingleAsync(Ct);

        Assert.Equal("changed", stored.Name);
        Assert.Equal(User, stored.LastSavedByUserID);
        Assert.NotEqual(stored.CreateDate, stored.LastSaveDate);
    }

    // ── A save made from inside a repository's save ─────────────────────────────

    /// <summary>After a voucher is saved, renames the memo that waits for it, in a save of its own.</summary>
    public sealed class MemoAfterAVoucher : IAfterSaveHandler<Voucher>
    {
        public async ValueTask AfterSaveAsync(SavedRows<Voucher> rows, CancellationToken cancellationToken)
        {
            var db = (Db)rows.Db;

            (await db.Memos.SingleAsync(memo => memo.Name == "waits", cancellationToken)).Name = "renamed in a nested save";
            await db.SaveChangesAsync(cancellationToken);
        }
    }

    /// <summary>Notes whether the save that is about to write a memo is a repository's.</summary>
    public sealed class MemoSeen(SaveLog log) : IBeforeSaveHandler<Memo>
    {
        public ValueTask BeforeSaveAsync(PendingRows<Memo> rows, CancellationToken cancellationToken)
        {
            foreach (var row in rows)
                log.Add($"'{row.Entity.Name}': {(rows.IsRepositorySave ? "a repository's save" : "not a repository's save")}");

            return ValueTask.CompletedTask;
        }
    }

    [Fact]
    public async Task A_save_made_from_inside_a_repository_save_is_not_a_repository_save()
    {
        await using var host = SavePipelineHost.Start(services => ForARepository(services).AddSaveHandler<MemoAfterAVoucher>().AddSaveHandler<MemoSeen>());

        await host.InScopeAsync(async db =>
        {
            db.AddRange(new Memo { Name = "in the save" }, new Memo { Name = "waits" });

            using (db.SuppressSaveHandler<MemoSeen>())
                await db.SaveChangesAsync(Ct);
        });

        await host.InScopeAsync(async db =>
        {
            (await db.Memos.SingleAsync(memo => memo.Name == "in the save", Ct)).Name = "in the save, changed";

            var repository = Vouchers(db);
            repository.Add(new Voucher { Series = "a", Number = 1 });
            await repository.SaveChangesAsync();
        });

        // A before-save handler is told which of the two saves is the repository's.
        Assert.Equal(
            ["'in the save, changed': a repository's save", "'renamed in a nested save': not a repository's save"],
            host.Log.Lines);

        // And only the repository's own save stamps a modified row.
        await using var bare = host.Bare();
        var stored = await bare.Memos.AsNoTracking().ToDictionaryAsync(memo => memo.Name!, Ct);

        Assert.NotEqual(stored["in the save, changed"].CreateDate, stored["in the save, changed"].LastSaveDate);
        Assert.Equal(stored["renamed in a nested save"].CreateDate, stored["renamed in a nested save"].LastSaveDate);
    }

    // ── The unique hash ─────────────────────────────────────────────────────────

    /// <summary>Adds a memo for a new voucher, so that the save has a second round.</summary>
    public sealed class MemoForAVoucher : IBeforeSaveHandler<Voucher>
    {
        public ValueTask BeforeSaveAsync(PendingRows<Voucher> rows, CancellationToken cancellationToken)
        {
            foreach (var row in rows)
                if (row.Change == RowChange.Added)
                    rows.Db.Add(new Memo { Name = "for a voucher" });

            return ValueTask.CompletedTask;
        }
    }

    /// <summary>In the round after: gives every new voucher of the save the number 7, a row an earlier round offered.</summary>
    public sealed class SevenInTheNextRound : IBeforeSaveHandler<Memo>
    {
        public ValueTask BeforeSaveAsync(PendingRows<Memo> rows, CancellationToken cancellationToken)
        {
            foreach (var voucher in rows.Db.ChangeTracker.Entries<Voucher>())
                if (voucher.State == EntityState.Added)
                    voucher.Entity.Number = 7;

            return ValueTask.CompletedTask;
        }
    }

    [Fact]
    public async Task The_unique_hash_is_computed_once_after_every_round_from_the_values_the_row_is_written_with()
    {
        await using var host = SavePipelineHost.Start(services => ForARepository(services).AddSaveHandler<MemoForAVoucher>().AddSaveHandler<SevenInTheNextRound>());

        await host.InScopeAsync(async db =>
        {
            var voucher = new Voucher { Series = "a", Number = 1 };
            var repository = Vouchers(db);
            repository.Add(voucher);
            await repository.SaveChangesAsync();

            Assert.Equal(7, voucher.Number);
            Assert.Equal(SHA256.HashData(Encoding.UTF8.GetBytes("a|7")), (byte[]?)db.Entry(voucher).Property("UniqueHash").CurrentValue);
        });

        // So the index refuses a second voucher that ends with the same number, whatever number its caller gave it.
        await host.InScopeAsync(async db =>
        {
            var repository = Vouchers(db);
            repository.Add(new Voucher { Series = "a", Number = 2 });
            await Assert.ThrowsAsync<DbUpdateException>(() => repository.SaveChangesAsync());
        });

        // A plain save computes no hash: the unique hash is the repository's.
        await host.InScopeAsync(async db =>
        {
            var voucher = new Voucher { Series = "a", Number = 3 };
            db.Add(voucher);
            await db.SaveChangesAsync(Ct);

            Assert.Null(db.Entry(voucher).Property("UniqueHash").CurrentValue);
        });
    }

    // ── The reload ──────────────────────────────────────────────────────────────

    /// <summary>After a voucher is saved, writes its memo with a statement the context does not track.</summary>
    public sealed class MemoWrittenBySql : IAfterSaveHandler<Voucher>
    {
        public async ValueTask AfterSaveAsync(SavedRows<Voucher> rows, CancellationToken cancellationToken)
        {
            foreach (var row in rows)
                if (row.Entity.MemoID is { } memo)
                    await rows.Db.Database.ExecuteSqlRawAsync("UPDATE \"Memos\" SET \"Name\" = 'written by the handler' WHERE \"ID\" = {0}", [memo], cancellationToken);
        }
    }

    [Fact]
    public async Task The_rows_loaded_with_includes_are_loaded_again_after_the_handlers_inside_the_saves_transaction()
    {
        await using var host = SavePipelineHost.Start(services => ForARepository(services).AddSaveHandler<MemoWrittenBySql>());

        await host.InScopeAsync(async db =>
        {
            var memo = new Memo { Name = "as saved" };
            db.Add(memo);
            await db.SaveChangesAsync(Ct);
            host.Sql.Clear();

            var repository = Vouchers(db, withMemo: true);
            repository.Add(new Voucher { Series = "a", Number = 1, MemoID = memo.ID });
            await repository.SaveChangesAsync();
        });

        // The write, the handler's statement, the reload, and then the commit.
        Assert.Equal(["BEGIN", "INSERT Vouchers", "UPDATE Memos", "SELECT", "COMMIT"], host.Sql.Lines);
    }

    [Fact]
    public async Task In_a_save_that_needs_no_transaction_the_reload_runs_after_the_write()
    {
        await using var host = SavePipelineHost.Start(services => ForARepository(services));

        await host.InScopeAsync(async db =>
        {
            var repository = Vouchers(db, withMemo: true);
            repository.Add(new Voucher { Series = "a", Number = 1 });
            await repository.SaveChangesAsync();
        });

        Assert.Equal(["INSERT Vouchers", "SELECT"], host.Sql.Lines);
    }

    // ── The endpoints ───────────────────────────────────────────────────────────

    /// <summary>Fails once a voucher that asks for it is committed: with words for the user, or with none.</summary>
    public sealed class FailsAfterCommit : IAfterCommitHandler<Voucher>
    {
        public const string WithAMessage = "fails with a message";
        public const string WithoutOne = "fails";

        public ValueTask AfterCommitAsync(SavedRows<Voucher> rows, CancellationToken cancellationToken)
        {
            if (rows.Any(row => row.Entity.Name == WithAMessage))
                throw new ShiftEntityException(new Message("Not sent", "The voucher is saved, but its email could not be sent."));

            if (rows.Any(row => row.Entity.Name == WithoutOne))
                throw new InvalidOperationException("The connection to the mail server was refused.");

            return ValueTask.CompletedTask;
        }
    }

    /// <summary>Lets every row through, where the stand-in the other tests use lets none through a query.</summary>
    private sealed class EveryRow : IDefaultDataLevelAccess
    {
        public IQueryable<EntityType> ApplyDefaultDataLevelFilters<EntityType>(DefaultDataLevelAccessOptions options, IQueryable<EntityType> query)
            where EntityType : notnull => query;

        public bool HasDefaultDataLevelAccess<EntityType>(DefaultDataLevelAccessOptions options, EntityType? entity, TypeAuth.Core.Access access)
            where EntityType : ShiftEntity<EntityType>, new() => true;

        public List<long?>? GetAccessibleCountries() => throw new NotSupportedException();
        public List<long?>? GetAccessibleRegions() => throw new NotSupportedException();
        public List<long?>? GetAccessibleCompanies() => throw new NotSupportedException();
        public List<long?>? GetAccessibleBranches() => throw new NotSupportedException();
        public List<long?>? GetAccessibleBrands() => throw new NotSupportedException();
        public List<long?>? GetAccessibleCities() => throw new NotSupportedException();
        public List<long?>? GetAccessibleTeams() => throw new NotSupportedException();

        public IQueryable<EntityType> ApplyDefaultCountryFilter<EntityType>(IQueryable<EntityType> query) where EntityType : global::ShiftSoftware.ShiftEntity.Model.Flags.IEntityHasCountry<EntityType> => throw new NotSupportedException();
        public IQueryable<EntityType> ApplyDefaultRegionFilter<EntityType>(IQueryable<EntityType> query) where EntityType : global::ShiftSoftware.ShiftEntity.Model.Flags.IEntityHasRegion<EntityType> => throw new NotSupportedException();
        public IQueryable<EntityType> ApplyDefaultCompanyFilter<EntityType>(IQueryable<EntityType> query) where EntityType : global::ShiftSoftware.ShiftEntity.Model.Flags.IEntityHasCompany<EntityType> => throw new NotSupportedException();
        public IQueryable<EntityType> ApplyDefaultBranchFilter<EntityType>(IQueryable<EntityType> query) where EntityType : global::ShiftSoftware.ShiftEntity.Model.Flags.IEntityHasCompanyBranch<EntityType> => throw new NotSupportedException();
        public IQueryable<EntityType> ApplyDefaultBrandFilter<EntityType>(IQueryable<EntityType> query) where EntityType : global::ShiftSoftware.ShiftEntity.Model.Flags.IEntityHasBrand<EntityType> => throw new NotSupportedException();
        public IQueryable<EntityType> ApplyDefaultCityFilter<EntityType>(IQueryable<EntityType> query) where EntityType : global::ShiftSoftware.ShiftEntity.Model.Flags.IEntityHasCity<EntityType> => throw new NotSupportedException();
        public IQueryable<EntityType> ApplyDefaultTeamFilter<EntityType>(IQueryable<EntityType> query) where EntityType : global::ShiftSoftware.ShiftEntity.Model.Flags.IEntityHasTeam<EntityType> => throw new NotSupportedException();
    }

    private static IServiceCollection ForAnEndpoint(IServiceCollection services) => ForARepository(services)
        .AddSingleton<IDefaultDataLevelAccess>(new EveryRow())
        .AddScoped(provider => Vouchers(provider.GetRequiredService<Db>()));

    private static SavePipelineHost EndpointHost() => SavePipelineHost.Start(services => ForAnEndpoint(services).AddSaveHandler<FailsAfterCommit>());

    private static readonly ShiftEntityCrudHandler<ShiftRepository<Db, Voucher, VoucherDto, VoucherDto>, Voucher, VoucherDto, VoucherDto> Endpoint = new();

    [Theory]
    [InlineData(FailsAfterCommit.WithAMessage, "Not sent", "The voucher is saved, but its email could not be sent.")]
    [InlineData(FailsAfterCommit.WithoutOne, "Saved", "The record was saved, but a follow-up step failed.")]
    public async Task An_endpoint_answers_a_save_whose_follow_up_failed_as_saved_with_the_record_the_failure_as_its_message_and_a_marker(
        string name, string title, string body)
    {
        await using var host = EndpointHost();
        await using var scope = host.Provider.CreateAsyncScope();
        var http = new DefaultHttpContext { RequestServices = scope.ServiceProvider };

        var (result, saved) = await Endpoint.PostAsync(http, new VoucherDto { Name = name });

        // Created, as for any insert: the record is in the database, and nothing is undone.
        Assert.Equal(StatusCodes.Status201Created, result.StatusCode);
        Assert.NotNull(saved);

        var response = Assert.IsType<ShiftEntityResponse<VoucherDto>>(result.Body);

        Assert.Equal(saved.ID.ToString(), response.Entity!.ID);
        Assert.Equal(name, response.Entity.Name);

        // What failed is the message: the failure's own words when it has words for the user, and general ones
        // when it is any other exception, whose text is not for a user.
        Assert.Equal(title, response.Message!.Title);
        Assert.Equal(body, response.Message.Body);

        // A client that is not a person reads the marker.
        Assert.Equal(true, response.Additional![ShiftEntityResponse.SavedWithFailure]);

        await using var bare = host.Bare();
        Assert.Equal(name, (await bare.Vouchers.AsNoTracking().SingleAsync(Ct)).Name);

        // The failure is logged, whatever the client makes of the answer.
        Assert.Contains(host.Logs.Entries, entry => entry.Level == Microsoft.Extensions.Logging.LogLevel.Error &&
            entry.Message.Contains("A Voucher was saved, but a step that runs after the commit failed."));
    }

    [Fact]
    public async Task An_edit_and_a_delete_whose_follow_up_failed_are_answered_the_same_way_and_a_save_that_went_well_has_no_marker()
    {
        await using var host = EndpointHost();
        await using var scope = host.Provider.CreateAsyncScope();
        var http = new DefaultHttpContext { RequestServices = scope.ServiceProvider };

        var (created, saved) = await Endpoint.PostAsync(http, new VoucherDto { Name = "fine" });
        var first = Assert.IsType<ShiftEntityResponse<VoucherDto>>(created.Body);

        Assert.Equal(StatusCodes.Status201Created, created.StatusCode);
        Assert.Null(first.Message);
        Assert.True(first.Additional is null || !first.Additional.ContainsKey(ShiftEntityResponse.SavedWithFailure));

        var key = saved!.ID.ToString();
        var edit = first.Entity!;
        edit.Name = FailsAfterCommit.WithAMessage;

        var (edited, _) = await Endpoint.PutAsync(http, key, edit);
        var second = Assert.IsType<ShiftEntityResponse<VoucherDto>>(edited.Body);

        Assert.Equal(StatusCodes.Status200OK, edited.StatusCode);
        Assert.Equal(FailsAfterCommit.WithAMessage, second.Entity!.Name);
        Assert.Equal("Not sent", second.Message!.Title);
        Assert.Equal(true, second.Additional![ShiftEntityResponse.SavedWithFailure]);

        var (deleted, _) = await Endpoint.DeleteAsync(http, key);
        var third = Assert.IsType<ShiftEntityResponse<VoucherDto>>(deleted.Body);

        Assert.Equal(StatusCodes.Status200OK, deleted.StatusCode);
        Assert.Equal("Not sent", third.Message!.Title);
        Assert.Equal(true, third.Additional![ShiftEntityResponse.SavedWithFailure]);

        await using var bare = host.Bare();
        Assert.True((await bare.Vouchers.AsNoTracking().SingleAsync(Ct)).IsDeleted);
    }

    [Fact]
    public async Task A_failure_before_the_commit_is_the_error_answer_it_always_was_and_nothing_is_saved()
    {
        await using var host = SavePipelineHost.Start(services => ForAnEndpoint(services)
            .AddScript(new Script<Voucher>
            {
                AfterSave = (_, _) => throw new ShiftEntityException(new Message("Refused", "No voucher today."), StatusCodes.Status409Conflict),
            }));

        await using var scope = host.Provider.CreateAsyncScope();
        var http = new DefaultHttpContext { RequestServices = scope.ServiceProvider };

        var (result, saved) = await Endpoint.PostAsync(http, new VoucherDto { Name = "refused" });
        var response = Assert.IsType<ShiftEntityResponse<VoucherDto>>(result.Body);

        Assert.Equal(StatusCodes.Status409Conflict, result.StatusCode);
        Assert.Null(saved);
        Assert.Null(response.Entity);
        Assert.Equal("Refused", response.Message!.Title);

        await using var bare = host.Bare();
        Assert.Empty(await bare.Vouchers.AsNoTracking().ToListAsync(Ct));
    }

    // ── Inside a caller's transaction ───────────────────────────────────────────

    [Fact]
    public async Task A_repository_save_inside_a_transaction_of_the_callers_joins_it_and_opens_none_of_its_own()
    {
        // An after-save handler makes a save need a transaction. The repository used to open one of its own for its
        // after-save hook, which a caller's transaction clashed with.
        await using var host = SavePipelineHost.Start(services => ForARepository(services).AddSaveHandler<StageLog<Voucher>>());

        await host.InScopeAsync(async db =>
        {
            await using var transaction = await db.Database.BeginTransactionAsync(Ct);
            var repository = Vouchers(db);

            repository.Add(new Voucher { Series = "a", Number = 1, Name = "first" });
            await repository.SaveChangesAsync();

            repository.Add(new Voucher { Series = "a", Number = 2, Name = "second" });
            await repository.SaveChangesAsync();

            Assert.Equal(["after save of Voucher: Added Voucher 'first'", "after save of Voucher: Added Voucher 'second'"], host.Log.Lines);

            await transaction.CommitAsync(Ct);
        });

        Assert.Equal(
        [
            "after save of Voucher: Added Voucher 'first'",
            "after save of Voucher: Added Voucher 'second'",
            "before commit of Voucher: Added Voucher 'first', Added Voucher 'second'",
            "after commit of Voucher: Added Voucher 'first', Added Voucher 'second'",
        ], host.Log.Lines);

        Assert.Single(host.Sql.Lines, "BEGIN");
        Assert.Single(host.Sql.Lines, "COMMIT");
        Assert.DoesNotContain("ROLLBACK", host.Sql.Lines);
    }
}
