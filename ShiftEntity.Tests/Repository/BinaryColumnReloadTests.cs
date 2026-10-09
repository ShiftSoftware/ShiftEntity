using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using ShiftSoftware.ShiftEntity.Core;
using ShiftSoftware.ShiftEntity.EFCore;
using ShiftSoftware.ShiftEntity.Model;
using ShiftSoftware.ShiftEntity.Tests.Auditing.Scenario;
using ShiftSoftware.ShiftEntity.Tests.DataLevelAccess.Scenario;
using ShiftSoftware.ShiftEntity.Tests.DataProject;
using Xunit;

namespace ShiftSoftware.ShiftEntity.Tests.Repository;

/// <summary>
/// A nullable binary column through the save path of a repository with includes, on the maps ShiftMapper's generator
/// wrote (<see cref="DataProject"/>), against SQLite.
///
/// <para>A save of such a repository reloads the entity and copies the fresh load onto the tracked one with the
/// entity's copy map. When that map turned a null <c>byte[]</c> into an empty one, the tracked entity changed, and the
/// next save in the same request stored <c>0x</c> where the row had NULL. A user created through the identity
/// dashboard got <c>TotpSecret = 0x</c> this way, and the next start of an identity authority refused to run.</para>
/// </summary>
public class BinaryColumnReloadTests : IDisposable
{
    private static readonly byte[] SecretBytes = [1, 2, 3, 4];

    private readonly SqliteConnection connection = new("DataSource=:memory:");
    private readonly ServiceProvider provider;

    public BinaryColumnReloadTests()
    {
        connection.Open();

        var services = new ServiceCollection();
        services.AddDbContext<DataProjectDb>(o => o.UseSqlite(connection));
        services.AddScoped<ICurrentUserProvider>(_ => FakeUserProvider.Anonymous());
        services.AddScoped<IdentityClaimProvider>();
        services.AddSingleton<IHashIdService>(new IdentityHashIdService());
        services.AddSingleton<IDefaultDataLevelAccess>(new RecordingDefaultDataLevelAccess());
        services.AddSingleton(new ShiftEntityOptions());
        // As a host registers its data project: the repositories, and the generated mapper that declares their maps.
        services.RegisterShiftRepositories(typeof(CredentialRepository).Assembly);
        provider = services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true });

        using var scope = provider.CreateScope();
        scope.ServiceProvider.GetRequiredService<DataProjectDb>().Database.EnsureCreated();
    }

    public void Dispose()
    {
        provider.Dispose();
        connection.Dispose();
    }

    /// <summary>The column as the database holds it, read with a context of its own.</summary>
    private async Task<byte[]?> StoredSecret(long id)
    {
        using var scope = provider.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<DataProjectDb>().Credentials
            .AsNoTracking().Where(x => x.ID == id).Select(x => x.Secret).SingleAsync();
    }

    private async Task<long> Create(byte[]? secret)
    {
        using var scope = provider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<DataProjectDb>();
        var credential = new Credential { Name = "existing", Secret = secret };
        db.Credentials.Add(credential);
        await db.SaveChangesAsync();
        return credential.ID;
    }

    [Fact]
    public async Task A_create_keeps_a_null_binary_column_null_after_the_reload_and_a_second_save()
    {
        using var scope = provider.CreateScope();
        var repository = scope.ServiceProvider.GetRequiredService<CredentialRepository>();

        // The dashboard's create: map the form onto a new entity, add it, save.
        var credential = await repository.UpsertAsync(new Credential(), new CredentialDTO { Name = "created" },
            ActionTypes.Insert, userId: null, idempotencyKey: null, disableDefaultDataLevelAccess: true, disableGlobalFilters: true);
        repository.Add(credential);
        Assert.True(credential.ReloadAfterSave);
        await repository.SaveChangesAsync();

        // The reload copied the fresh row onto the tracked entity, and left nothing to save.
        Assert.Null(credential.Secret);
        Assert.Equal(EntityState.Unchanged, scope.ServiceProvider.GetRequiredService<DataProjectDb>().Entry(credential).State);

        // A later save in the same request, as the identity dashboard's create makes.
        credential.Name = "created and saved again";
        await repository.SaveChangesAsync();

        Assert.Null(await StoredSecret(credential.ID));
    }

    [Fact]
    public async Task An_update_keeps_a_null_binary_column_null_and_copies_a_stored_one()
    {
        var empty = await Create(secret: null);
        var stored = await Create(secret: [.. SecretBytes]);

        using var scope = provider.CreateScope();
        var repository = scope.ServiceProvider.GetRequiredService<CredentialRepository>();

        foreach (var id in new[] { empty, stored })
        {
            // The dashboard's edit: find with the includes, map the form onto it, save twice.
            var credential = (await repository.FindAsync(id, asOf: null, disableDefaultDataLevelAccess: true, disableGlobalFilters: true))!;
            var before = credential.Secret;
            await repository.UpsertAsync(credential, new CredentialDTO { ID = id.ToString(), Name = "edited" },
                ActionTypes.Update, userId: null, idempotencyKey: null, disableDefaultDataLevelAccess: true, disableGlobalFilters: true);
            await repository.SaveChangesAsync();

            if (before is null) Assert.Null(credential.Secret);
            else Assert.Equal(SecretBytes, credential.Secret);

            credential.Name = "edited and saved again";
            await repository.SaveChangesAsync();
        }

        Assert.Null(await StoredSecret(empty));
        Assert.Equal(SecretBytes, await StoredSecret(stored));
    }
}
