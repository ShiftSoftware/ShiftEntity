using System;

namespace ShiftSoftware.ShiftEntity.Model.Replication;

/// <summary>
/// The ItemType should be an Item that contains id property of type string.
/// </summary>
[AttributeUsage(AttributeTargets.Class, AllowMultiple = false)]
public class ShiftEntityReplicationAttribute : Attribute
{
    /// <summary>
    /// There should be an auto-mapper mapping from the entity to this Item, 
    /// and contains id property of type string.
    /// </summary>
    public Type ItemType { get; set; }

    /// <summary>
    /// If null, gets the entity name
    /// </summary>
    public string? ContainerName { get; set; }

    /// <summary>
    /// If null, gets the default database of the selected account
    /// </summary>
    public string? DatabaseName { get; set; }

    /// <summary>
    /// Name of the account that registerd, if null gets the default account
    /// </summary>
    public string? AccountName { get; set; }

    internal (string containerName, string connectionString, string databaseName) GetConfigurations
        (IEnumerable<CosmosDBAccount> accounts, string entityName)
    {
        string container;
        string connectionString;
        string databaseName;
        CosmosDBAccount? account;

        container = ContainerName ?? entityName;

        if (AccountName is null)
        {
            if (!accounts.Any(x => x.IsDefault))
                throw new ArgumentException("No account specified");
            else
            {
                account = accounts.FirstOrDefault(x => x.IsDefault);
                connectionString = account!.ConnectionString;
            }
        }
        else
        {
            account = accounts.FirstOrDefault(x => x.Name.ToLower() == AccountName.ToLower());
            if (account is null)
                throw new ArgumentException($"Can not find any account by name '{AccountName}'");
            else
                connectionString = account.ConnectionString;
        }

        if (DatabaseName is null && account.DefaultDatabaseName is null)
            throw new ArgumentException("No database specified");
        else
            databaseName = DatabaseName! ?? account.DefaultDatabaseName!;

        return (container, connectionString, databaseName);
    }
}

/// <summary>
/// It sets the id automatically from the shift entity childes
/// </summary>
/// <typeparam name="Item">There should be an auto-mapper mapping from the entity to this Item, 
/// and contains id property of type string.</typeparam>
[AttributeUsage(AttributeTargets.Class, AllowMultiple = false)]
public class ShiftEntityReplicationAttribute<Item> : ShiftEntityReplicationAttribute
{
    /// <summary>
    /// There should be an auto-mapper mapping from the entity to this Item, 
    /// and contains id property of type string.
    /// </summary>
    public new Type ItemType { get; private set; }

    public ShiftEntityReplicationAttribute()
    {
        this.ItemType = typeof(Item);
        base.ItemType = typeof(Item);
    }
}