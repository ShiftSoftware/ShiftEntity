using Microsoft.EntityFrameworkCore;
using ShiftSoftware.ShiftEntity.EFCore;

namespace ShiftSoftware.ShiftEntity.CosmosDbReplication;

internal class DbContextProvider
{
    private readonly Type dbContextType;
    private readonly object dbOptions;

    public DbContextProvider(Type dbContextType, object  dbOptions)
    {
        this.dbOptions = dbOptions;
        this.dbContextType = dbContextType;
    }

    public ShiftDbContext ProvideDbContext()
    {
        // The parameter to pass to the constructor
        object[] parameters = { dbOptions };

        // Create an instance of the class using reflection
        object? instance = Activator.CreateInstance(dbContextType, parameters);


        return (ShiftDbContext)instance;
    }
}
