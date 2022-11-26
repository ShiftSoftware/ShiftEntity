### CreateShiftEntity
The ``CreateShiftEntity`` method is used to intilize the properties of the ``ShiftEntity`` class.

``` C#
protected EntityType CreateShiftEntity(Guid? userId = null)
    {
        var now = DateTime.UtcNow;

        LastSaveDate = now;
        CreateDate = now;

        CreatedByUserID = userId;
        LastSavedByUserID = userId;

        IsDeleted = false;

        return this as EntityType;
    }
```

Parameters:

| Parameter                  | Description                                                                                          |
| ----------------------     | ---------------------------------------------------------------------------------------------------- |
| `userId`      | `Guid?` <br/> the ID of the user who created the object <br/> default: `null`|
### CreateShiftEntity
The ``CreateShiftEntity`` method is used to intilize the properties of the ``ShiftEntity`` class.

``` C#

protected EntityType CreateShiftEntity(Guid? userId = null)
    {
        var now = DateTime.UtcNow;

        LastSaveDate = now;
        CreateDate = now;

        CreatedByUserID = userId;
        LastSavedByUserID = userId;

        IsDeleted = false;

        return this as EntityType;
    }
```

Parameters:

| Parameter                  | Description                                                                                          |
| ----------------------     | ---------------------------------------------------------------------------------------------------- |
| `userId`      | `Guid?` <br/> the ID of the user who created the object <br/> default: `null`|

### Create
The ``Create`` method is an ``abstract`` method that will be used for initialization of the properties of the model that inherits from ``ShiftEntity`` class and the initilization of the base class properties

``` C#
public override TestItem Create(TestItemCrudDTO crudDto, Guid? userId = null)
        {
            this.CreateShiftEntity(userId);

            this.Name = crudDto.Name;

            return this;
        }
```

Parameters:

| Parameter                  | Description                                                                                          |
| ----------------------     | ---------------------------------------------------------------------------------------------------- |
| `crudDto`          | `CrudDTOType` <br/> the `crudDto` is an object of the Data transfer object(DTO) model that will be used only for data transfer purpose, it will be used to set the private fields of the main model |
| `userId`      | `Guid?` <br/> the ID of the user who created the object <br/> default: `null`|

### Update
The ``Update`` method is an ``abstract`` method that will be used for the modificatio of the properties of the model that inherits from ``ShiftEntity`` class

``` C#
public override TestItem Update(TestItemCrudDTO crudDto, Guid? userId = null)
        {
            this.UpdateShiftEntity(userId);

            this.Name = crudDto.Name;

            return this;
        }
```

Parameters:

| Parameter                  | Description                                                                                          |
| ----------------------     | ---------------------------------------------------------------------------------------------------- |
| `crudDto`          | `CrudDTOType` <br/> the `crudDto` is an object of the Data transfer object(DTO) model that will be used only for data transfer purpose, it will be used to set the private fields of the main model |
| `userId`      | `Guid?` <br/> the ID of the user who modified the object <br/> default: `null`|


### Delete
The ``Delete`` method is an ``abstract`` method that will be used to remove the properties of the model that inherits from ``ShiftEntity`` class

``` C#
public override TestItem Update(TestItemCrudDTO crudDto, Guid? userId = null)
        {
            this.UpdateShiftEntity(userId);

            this.Name = crudDto.Name;

            return this;
        }
```

Parameters:

| Parameter                  | Description                                                                                          |
| ----------------------     | ---------------------------------------------------------------------------------------------------- |
| `crudDto`          | `CrudDTOType` <br/> the `crudDto` is an object of the Data transfer object(DTO) model that will be used only for data transfer purpose, it will be used to set the private fields of the main model |
| `userId`      | `Guid?` <br/> the ID of the user who modified the object <br/> default: `null`|

!!! tip
    
    When aggregating. It's very important to Include the total count of the data. Something like ``Count = x.Count()``.   
    If you do this. We'll use your ``Count`` as the ``DataCount`` for the [`Grid`](/reference/#grid).
       
    This means there'll be 2 Database calls. One for getting the paginated data. And one for getting the aggrecated data.   
       
    If you don't Include the ``Count``. We'll add another database call for getting the ``Count``. And you'll have 3 Database calls instead of 2.


!!! danger
    
    If you do include the ``Count``. Make sure you do a full count and not a conditional count. If you something like below for example, the ``Grid`` will use your count as the ``DataCount`` leaving you with unexpected behaviour. 
    ``` C#
    .SelectAggregate(x => new
        {
            //This is very dangerous
            Count = x.Count(y=> y.ID > 10),
        }
    ```

    Do below instead
    ``` C#
    .SelectAggregate(x => new
        {
            //This is safe and recommended
            Count = x.Count(),
        }
    ```
    


### ToCSVStream
When the ``Export`` flag on [ExportConfig](/reference/#exportconfig) is set to true. This method ``ToCSVStream()`` can be used to export the entire data (Unpaginated) to a stream.

Here's an example:
``` C#
[HttpGet("export")]
public async Task<ActionResult> Export()
{
    var db = new DB();

    var DbF = Microsoft.EntityFrameworkCore.EF.Functions;

    var shiftGrid =
        await db
        .Employees
        .Select(x => new EmployeeCSV
        {
            ID = x.ID,
            FullName = x.FirstName + " " + x.LastName,
            Age = DbF.DateDiffYear(x.Birthdate, DateTime.Now)
        })
        .ToShiftGridAsync("ID", SortDirection.Ascending, new GridConfig
        {
            ExportConfig = new ExportConfig
            {
                Export = true,
            }
        });

    var stream = shiftGrid.ToCSVStream();

    return File(stream.ToArray(), "text/csv");
}
```

### ToCSVString
Identical to [ToCSVStream](#tocsvstream). But this will export the data to a ``String`` instead of a ``Stream``.
Here's an example for that. Note the Delimiter is changed to ``|`` in this example.
``` C#
[HttpGet("export-string")]
public async Task<ActionResult> ExportString()
{
    var db = new DB();

    var DbF = Microsoft.EntityFrameworkCore.EF.Functions;

    var shiftGrid =
        await db
        .Employees
        .Select(x => new EmployeeCSV
        {
            ID = x.ID,
            FullName = x.FirstName + " " + x.LastName,
            Age = DbF.DateDiffYear(x.Birthdate, DateTime.Now)
        })
        .ToShiftGridAsync("ID", SortDirection.Ascending, new GridConfig
        {
            ExportConfig = new ExportConfig
            {
                Export = true,
                Delimiter = "|"
            }
        });

    var csvString = shiftGrid.ToCSVString();

    return Ok(csvString);
}
```

!!! note

    We're using the [`FileHelpers`](https://www.filehelpers.net/) for exporting data to CSV.  
       
    In the above example we're using a class named ``EmployeeCSV``. Note how the class and the fields are decorated by custom attributes from ``FileHelpers`.
    ``` C#
    [FileHelpers.DelimitedRecord(",")]
    public class EmployeeCSV
    {
        [FileHelpers.FieldCaption("Employee ID")]
        public long ID { get; set; }

        [FileHelpers.FieldCaption("Full Name")]
        public string FullName { get; set; }

        [FileHelpers.FieldCaption("Age")]
        public int? Age { get; set; }
    }
    ```