using Microsoft.EntityFrameworkCore;
using ShiftSoftware.ShiftEntity.Core;
using ShiftSoftware.ShiftEntity.EFCore;
using ShiftSoftware.ShiftEntity.Model.Dtos;

namespace ShiftSoftware.ShiftEntity.Tests.DataProject;

/// <summary>
/// An entity with a nullable binary column and a child collection. The repository includes the children, so a save
/// reloads the entity and copies it onto the tracked one with the generated copy map (ReloadAfterSave). The DTOs do
/// not carry the binary column, as a user form does not carry a user's legacy factor: only the copy map touches it.
/// </summary>
public class Credential : ShiftEntity<Credential>
{
    public string Name { get; set; } = "";

    /// <summary>A varbinary column. NULL means "no secret", and an empty value would mean something else.</summary>
    public byte[]? Secret { get; set; }

    public List<CredentialNote> Notes { get; set; } = new();
}

public class CredentialNote : ShiftEntity<CredentialNote>
{
    public string Text { get; set; } = "";

    public long CredentialID { get; set; }
}

public class CredentialDTO : ShiftEntityViewAndUpsertDTO
{
    public override string? ID { get; set; }

    public string Name { get; set; } = "";
}

public class CredentialListDTO : ShiftEntityDTOBase
{
    public override string? ID { get; set; }

    public string Name { get; set; } = "";
}

/// <summary>A repository with includes, like most data projects' repositories.</summary>
public class CredentialRepository : ShiftRepository<DataProjectDb, Credential, CredentialListDTO, CredentialDTO>
{
    public CredentialRepository(DataProjectDb db) : base(db, r =>
        r.IncludeRelatedEntitiesWithFindAsync(x => x.Include(y => y.Notes)))
    {
    }
}

public class DataProjectDb : ShiftDbContext
{
    public DbSet<Credential> Credentials { get; set; } = default!;

    public DbSet<CredentialNote> CredentialNotes { get; set; } = default!;

    public DataProjectDb(DbContextOptions<DataProjectDb> options) : base(options) { }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

        modelBuilder.Entity<Credential>()
            .HasMany(x => x.Notes)
            .WithOne()
            .HasForeignKey(x => x.CredentialID);
    }
}
