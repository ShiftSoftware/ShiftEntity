using Microsoft.EntityFrameworkCore;
using ShiftSoftware.ShiftEntity.EFCore;

namespace ShiftSoftware.ShiftEntity.CosmosDbReplication;

internal class DbContextProvider
{
    private readonly Type dbContextType;
    private readonly DbContextOptions dbOptions;

    public DbContextProvider(Type dbContextType, Action<DbContextOptionsBuilder> dbOptionsBuilder)
    {
        DbContextOptionsBuilder optionsBuilder = new();
        dbOptionsBuilder.Invoke(optionsBuilder);

        dbOptions = optionsBuilder.Options;
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
