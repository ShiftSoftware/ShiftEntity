using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading.Tasks;

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

    public List<IDbContextProvider> DbContextProviders { get;private set; }

    public ShiftEntityCosmosDbOptions()
    {
        Accounts = new();
        DbContextProviders = new();
    }

    public ShiftEntityCosmosDbOptions AddDbContextProvider(IDbContextProvider provider)
    {
        this.DbContextProviders.Add(provider);
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
