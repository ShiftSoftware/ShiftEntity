### Stable Sort
Some database Engines do not guarantee a stable ordering of rows by default (For example: MS SQL Server).  

When paginating, the row orders might change. This is very bad of course because some rows might be repeated and some might not show up at all.


To solve this, The database ``ORDER BY`` must contains a column or combination of columns that are guaranteed to be unique.

#### Sort is not specified.

Even if you don't specify the Sort in the [GridConfig](/reference/#gridconfig). We enforce a stable sort in our [ToShiftGridAsync](/methods/#toshiftgridasync-toshiftgrid) and [ToShiftGrid](/methods/#toshiftgridasync-toshiftgrid) methods.  
``` C#
[HttpPost("stable-sort")]
public async Task<ActionResult> StableSort()
{
    var db = new DB();

    var shiftGrid =
        await db
        .Employees
        .ToShiftGridAsync("ID", SortDirection.Ascending);
}
```

The above example (when using EF Core and SQL Server) generates an SQL like below
``` SQL
SELECT 
TOP(20) 
[e].[ID], 
[e].[Birthdate], 
[e].[DepartmentId], 
[e].[FirstName], 
[e].[LastName]
FROM 
[Employees] AS [e]
ORDER BY [e].[ID]
```

#### Sorting is Specified

If you do specify the Sort in the [GridConfig](/reference/#gridconfig). Your Sort(s) are used first. And then the Stable Sort is used. (See the generated SQL for the below example).  
``` C#
[HttpPost("stable-sort-with-another-sort")]
public async Task<ActionResult> StableSortWithAnotherSort()
{
    var db = new DB();

    var shiftGrid =
        await db
        .Employees
        .ToShiftGridAsync(
        "ID",
        SortDirection.Ascending,
        new GridConfig
        {
            Sort = new List<GridSort> {
                new GridSort
                {
                    Field = nameof(Employee.Birthdate),
                    SortDirection = SortDirection.Descending
                }
            }
        });
}
```
The above example (when using EF Core and SQL Server) generates an SQL like below
``` SQL
SELECT TOP(20) 
[e].[ID], 
[e].[Birthdate], 
[e].[DepartmentId], 
[e].[FirstName], 
[e].[LastName]
FROM [Employees] AS [e]
ORDER BY [e].[Birthdate] DESC, [e].[ID]
```

!!! warning
    
    It's very important that you use a column or combination of columns that are guaranteed to be **unique**.  
    Otherwise the ordering and the pagination can not be guaranteed