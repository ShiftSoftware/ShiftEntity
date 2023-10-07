using System;
using System.Collections.Generic;
using System.Text;

namespace ShiftSoftware.ShiftEntity.Model;

public class CosmosDBAccount
{
    public string ConnectionString { get; set; }
    public string Name { get; set; }
    public bool IsDefault { get; set; }
    public string? DefaultDatabaseName { get; set; }

    public CosmosDBAccount()
    {
    }

    public CosmosDBAccount(string connectionString, string name, bool isDefault, string? defaultDatabaseName = null)
    {
        ConnectionString = connectionString;
        Name = name;
        IsDefault = isDefault;
        DefaultDatabaseName = defaultDatabaseName;
    }
}
