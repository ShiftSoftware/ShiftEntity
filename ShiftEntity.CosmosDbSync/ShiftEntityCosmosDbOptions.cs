using Microsoft.EntityFrameworkCore;
using ShiftSoftware.ShiftEntity.EFCore;
using System.Reflection;

namespace ShiftSoftware.ShiftEntity.CosmosDbSync;

public class ShiftEntityCosmosDbOptions
{
    /// <summary>
    /// This is become the default connection
    /// </summary>
    public string? ConnectionString { get; set; }

    /// <summary>
    /// This is become the default database for the specified connection string
    /// </summary>
    public string? DefaultDatabaseName { get; set; }

    /// <summary>
    /// If not set, it gets the entry assembly
    /// </summary>
    public Assembly? RepositoriesAssembly { get; set; }

    public List<CosmosDBAccount> Accounts { get; set; }

    internal List<ShiftDbContextStore> ShiftDbContextStorage { get; private set; }

    public ShiftEntityCosmosDbOptions()
    {
        Accounts = new();
        ShiftDbContextStorage = new();
    }

    public ShiftEntityCosmosDbOptions AddShiftDbContext<T>(Action<DbContextOptionsBuilder> optionBuilder)
        where T : ShiftDbContext
    {
        this.ShiftDbContextStorage.Add(new ShiftDbContextStore(typeof(T), optionBuilder));
        return this;
    }
}

public class CosmosDBAccount
{
    public string ConnectionString { get; set; }
    public string Name { get; set; }
    public bool IsDefault { get; set; }
    public string? DefaultDatabaseName { get; set; }

    public CosmosDBAccount()
    {
    }

    public CosmosDBAccount(string connectionString, string name, bool isDefault, string? defaultDatabaseName=null)
    {
        this.ConnectionString= connectionString;
        this.Name = name;
        this.IsDefault = isDefault;
        this.DefaultDatabaseName = defaultDatabaseName;
    }
}

internal class ShiftDbContextStore
{
    public Type ShiftDbContextType { get; private set; }
    public Action<DbContextOptionsBuilder> DbContextOptionsBuilder { get; private set; }

    public ShiftDbContextStore(Type shiftDbContextType, Action<DbContextOptionsBuilder> dbContextOptionsBuilder)
    {
        this.ShiftDbContextType = shiftDbContextType;
        this.DbContextOptionsBuilder = dbContextOptionsBuilder;
    }
}
