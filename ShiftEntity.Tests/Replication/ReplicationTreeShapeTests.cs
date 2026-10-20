using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using ShiftSoftware.ShiftEntity.Core;
using ShiftSoftware.ShiftEntity.EFCore;
using ShiftSoftware.ShiftEntity.EFCore.Replication;
using ShiftSoftware.ShiftEntity.Model.Replication;
using Xunit;

namespace ShiftSoftware.ShiftEntity.Tests.Replication;

/// <summary>
/// Pins the shapes a replication tree may have. A save finds the roots of a changed row after the write, from the
/// roots' side, and that finds every root that loses a row only when the tree follows references toward the row each
/// foreign key points at, and collections only directly under the root whose rows name the root by a foreign key
/// property of their own (link rows). Any other shape left the roots that lose a deleted or moved row stale and looking
/// replicated; it is refused when the model is built now. A branch's tree is of the supported shape.
/// </summary>
public class ReplicationTreeShapeTests
{
    public sealed class Root : ShiftEntity<Root>, IShiftEntityReplication
    {
        public long? CompanyID { get; set; }
        public Company? Company { get; set; }
        public List<Line> Lines { get; set; } = new();
        public Profile? Profile { get; set; }
        public List<Note> Notes { get; set; } = new();
        public long? PartID { get; set; }
        public KeyedPart? Part { get; set; }
        public DateTimeOffset? LastReplicationDate { get; set; }
        public string? LastReplicationStamp { get; set; }
    }

    public sealed class Company : ShiftEntity<Company>
    {
        public List<Department> Departments { get; set; } = new();
    }

    public sealed class Department : ShiftEntity<Department>
    {
        public long CompanyID { get; set; }
    }

    public sealed class Line : ShiftEntity<Line>
    {
        public long RootID { get; set; }
        public List<SubLine> SubLines { get; set; } = new();
    }

    public sealed class SubLine : ShiftEntity<SubLine>
    {
        public long LineID { get; set; }
    }

    // The dependent end of a one-to-one relationship: its foreign key points at the root.
    public sealed class Profile : ShiftEntity<Profile>
    {
        public long RootID { get; set; }
    }

    // A row that points at the root through a shadow foreign key.
    public sealed class Note : ShiftEntity<Note>
    {
    }

    // A part whose single long key is not named ID.
    public sealed class KeyedPart
    {
        public long Id { get; set; }
    }

    public abstract class ShapeDb(DbContextOptions options) : ShiftDbContext(options)
    {
        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            modelBuilder.Entity<Root>().HasMany(x => x.Lines).WithOne().HasForeignKey(x => x.RootID);
            modelBuilder.Entity<Line>().HasMany(x => x.SubLines).WithOne().HasForeignKey(x => x.LineID);
            modelBuilder.Entity<Company>().HasMany(x => x.Departments).WithOne().HasForeignKey(x => x.CompanyID);
            modelBuilder.Entity<Root>().HasOne(x => x.Profile).WithOne().HasForeignKey<Profile>(x => x.RootID);
            modelBuilder.Entity<Root>().HasMany(x => x.Notes).WithOne().HasForeignKey("RootID");
            base.OnModelCreating(modelBuilder);
            modelBuilder.Entity<Root>().HasReplicationTree(Tree);
        }

        protected abstract System.Linq.Expressions.Expression<Func<IQueryable<Root>, IQueryable<Root>>> Tree { get; }
    }

    public sealed class CollectionUnderCollectionDb(DbContextOptions<CollectionUnderCollectionDb> options) : ShapeDb(options)
    {
        protected override System.Linq.Expressions.Expression<Func<IQueryable<Root>, IQueryable<Root>>> Tree =>
            q => q.Include(x => x.Lines).ThenInclude(x => x.SubLines);
    }

    public sealed class CollectionUnderReferenceDb(DbContextOptions<CollectionUnderReferenceDb> options) : ShapeDb(options)
    {
        protected override System.Linq.Expressions.Expression<Func<IQueryable<Root>, IQueryable<Root>>> Tree =>
            q => q.Include(x => x.Company).ThenInclude(x => x!.Departments);
    }

    public sealed class DependentReferenceDb(DbContextOptions<DependentReferenceDb> options) : ShapeDb(options)
    {
        protected override System.Linq.Expressions.Expression<Func<IQueryable<Root>, IQueryable<Root>>> Tree =>
            q => q.Include(x => x.Profile);
    }

    public sealed class ShadowForeignKeyDb(DbContextOptions<ShadowForeignKeyDb> options) : ShapeDb(options)
    {
        protected override System.Linq.Expressions.Expression<Func<IQueryable<Root>, IQueryable<Root>>> Tree =>
            q => q.Include(x => x.Notes);
    }

    public sealed class KeyNotNamedIdDb(DbContextOptions<KeyNotNamedIdDb> options) : ShapeDb(options)
    {
        protected override System.Linq.Expressions.Expression<Func<IQueryable<Root>, IQueryable<Root>>> Tree =>
            q => q.Include(x => x.Part);
    }

    public sealed class SupportedDb(DbContextOptions<SupportedDb> options) : ShapeDb(options)
    {
        protected override System.Linq.Expressions.Expression<Func<IQueryable<Root>, IQueryable<Root>>> Tree =>
            q => q.Include(x => x.Company).Include(x => x.Lines);
    }

    private static TDb Context<TDb>(SqliteConnection connection) where TDb : DbContext =>
        (TDb)Activator.CreateInstance(typeof(TDb), new DbContextOptionsBuilder<TDb>().UseSqlite(connection).Options)!;

    private static string Refused<TDb>() where TDb : DbContext
    {
        using var connection = new SqliteConnection("DataSource=:memory:");
        using var db = Context<TDb>(connection);
        return Assert.Throws<InvalidOperationException>(() => db.Model).Message;
    }

    [Fact]
    public void A_collection_below_another_collection_is_refused_when_the_model_is_built() =>
        Assert.Contains("cannot include 'Lines.SubLines': a collection can only be the first step from the root", Refused<CollectionUnderCollectionDb>());

    [Fact]
    public void A_collection_below_a_reference_is_refused_when_the_model_is_built() =>
        Assert.Contains("cannot include 'Company.Departments': a collection can only be the first step from the root", Refused<CollectionUnderReferenceDb>());

    [Fact]
    public void A_reference_to_a_dependent_row_is_refused_when_the_model_is_built() =>
        Assert.Contains("cannot include 'Profile': 'Profile' depends on 'Root'", Refused<DependentReferenceDb>());

    [Fact]
    public void A_collection_whose_rows_point_at_the_root_through_a_shadow_foreign_key_is_refused_when_the_model_is_built() =>
        Assert.Contains("cannot include 'Notes'", Refused<ShadowForeignKeyDb>());

    // RV-17. The save and the mark find rows by a key named ID. A part with another key name used to pass and then
    // fail every save of it, after EF had accepted the save.
    [Fact]
    public void A_part_whose_key_is_not_named_ID_is_refused_when_the_model_is_built() =>
        Assert.Contains("needs a single long key property named ID, and the key of 'KeyedPart' is Id (Int64)", Refused<KeyNotNamedIdDb>());

    [Fact]
    public void References_and_collections_directly_under_the_root_are_accepted()
    {
        using var connection = new SqliteConnection("DataSource=:memory:");
        using var db = Context<SupportedDb>(connection);

        Assert.NotNull(db.Model);
        Assert.True(db.HasReplicationTree(typeof(Root)));
    }

    // The shape of the branch tree an identity context declares: its city, the city's region and the region's country;
    // its company; and its service, department and brand links, each with the row it links to.
    public sealed class Country : ShiftEntity<Country>
    {
        public string Name { get; set; } = "";
    }

    public sealed class Region : ShiftEntity<Region>
    {
        public long CountryID { get; set; }
        public Country? Country { get; set; }
    }

    public sealed class City : ShiftEntity<City>
    {
        public long RegionID { get; set; }
        public Region? Region { get; set; }
    }

    public sealed class Service : ShiftEntity<Service>
    {
        public string Name { get; set; } = "";
    }

    public sealed class Brand : ShiftEntity<Brand> { }

    public sealed class Branch : ShiftEntity<Branch>, IShiftEntityReplication
    {
        public string Name { get; set; } = "";
        public long? CityID { get; set; }
        public City? City { get; set; }
        public long CompanyID { get; set; }
        public Company? Company { get; set; }
        public List<BranchService>? BranchServices { get; set; }
        public List<BranchDepartment>? BranchDepartments { get; set; }
        public List<BranchBrand>? BranchBrands { get; set; }
        public DateTimeOffset? LastReplicationDate { get; set; }
        public string? LastReplicationStamp { get; set; }
    }

    public sealed class BranchService : ShiftEntity<BranchService>
    {
        public long BranchID { get; set; }
        public long ServiceID { get; set; }
        public Service Service { get; set; } = default!;
        public Branch Branch { get; set; } = default!;
    }

    public sealed class BranchDepartment : ShiftEntity<BranchDepartment>
    {
        public long BranchID { get; set; }
        public long DepartmentID { get; set; }
        public Department Department { get; set; } = default!;
        public Branch Branch { get; set; } = default!;
    }

    public sealed class BranchBrand : ShiftEntity<BranchBrand>
    {
        public long BranchID { get; set; }
        public long BrandID { get; set; }
        public Brand Brand { get; set; } = default!;
        public Branch Branch { get; set; } = default!;
    }

    public sealed class BranchDb(DbContextOptions<BranchDb> options) : ShiftDbContext(options)
    {
        public DbSet<Branch> Branches => Set<Branch>();

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            modelBuilder.Entity<Company>().HasMany(x => x.Departments).WithOne().HasForeignKey(x => x.CompanyID);
            base.OnModelCreating(modelBuilder);
            modelBuilder.Entity<Branch>().HasReplicationTree(q => q
                .Include(x => x.City).ThenInclude(x => x!.Region).ThenInclude(x => x!.Country)
                .Include(x => x.Company)
                .Include(x => x.BranchServices!).ThenInclude(x => x.Service)
                .Include(x => x.BranchDepartments!).ThenInclude(x => x.Department)
                .Include(x => x.BranchBrands!).ThenInclude(x => x.Brand));
        }
    }

    [Fact]
    public async Task A_branch_shaped_tree_is_accepted_and_a_rename_of_a_country_or_a_service_marks_its_branches()
    {
        var ct = TestContext.Current.CancellationToken;
        using var connection = new SqliteConnection("DataSource=:memory:");
        connection.Open();

        await using (var db = Context<BranchDb>(connection))
        {
            await db.Database.EnsureCreatedAsync(ct);
            var country = new Country();
            var service = new Service();
            db.Branches.AddRange(
                new Branch
                {
                    Name = "In the country, with the service",
                    City = new City { Region = new Region { Country = country } },
                    Company = new Company(),
                    BranchServices = [new BranchService { Service = service }],
                },
                new Branch { Name = "With the service", Company = new Company(), BranchServices = [new BranchService { Service = service }] },
                new Branch { Name = "Neither", Company = new Company() });
            await db.SaveChangesAsync(ct);
            await db.Branches.ExecuteUpdateAsync(s => s.SetProperty(x => x.LastReplicationDate, x => (DateTimeOffset?)x.LastSaveDate), ct);
        }

        // Renames the one row of a part type, with every branch replicated before, and gives the branches it marked.
        async Task<List<string>> RenameAsync<T>(Action<T> rename) where T : ShiftEntity<T>
        {
            await using (var db = Context<BranchDb>(connection))
            {
                await db.Branches.ExecuteUpdateAsync(s => s.SetProperty(x => x.LastReplicationDate, x => (DateTimeOffset?)x.LastSaveDate), ct);
                rename(await db.Set<T>().SingleAsync(ct));
                await db.SaveChangesAsync(ct);
            }

            await using (var db = Context<BranchDb>(connection))
                return await db.Branches.Where(x => x.LastReplicationDate == null).Select(x => x.Name).OrderBy(x => x).ToListAsync(ct);
        }

        Assert.Equal(["In the country, with the service"], await RenameAsync<Country>(x => x.Name = "Renamed"));
        Assert.Equal(["In the country, with the service", "With the service"], await RenameAsync<Service>(x => x.Name = "Renamed"));
    }
}
