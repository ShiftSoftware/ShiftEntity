The Shift Grid fetches data and prepares it in a format that can be easily integrated into a Data Table.

### ShiftEntity

The ``ShiftEntity`` (``ShiftSoftware.ShiftEntity.Core.ShiftEntity``) can be used on **Data Models** by inheriting from it to enhance the Model to a **Rich Domain Model**.  
     
The ``ShiftEntity`` contains below properties.

| Property                   | Description                                                                                          |
| ----------------------     | ---------------------------------------------------------------------------------------------------- |
| `ID`            | `Guid` <br/> The Identity of the model |
| `CreateDate`             | `DateTime` <br/> The timestamp of the object creation. |
| `LastSaveDate`                | `int` <br/> The timestamp of the last save or modification being done on the object. |
| `CreatedByUserID`                     | `Guid` <br/> The ID of the user who created the object.<br/>  |
| `LastSavedByUserID`                | `Guid` <br/> The ID of the user who made the latest change to the object. |
| `IsDeleted`                     | `bool` <br/> A flag to show soft delete status.<br/>  |

### GridConfig
The ``ToShiftGridAsync`` and ``ToShiftGrid`` extension methods accept a `GridConfig`.
This is used to control the Grid. Like setting the page size, index, sorting, filters ...etc. Below are the properties.<br/>

| Property                   | Description                                                                                          |
| ----------------------     | ---------------------------------------------------------------------------------------------------- |
| `DataPageIndex`            | `int` <br/> Sets the page index of the paginated data.  |
| `DataPageSize`             | `int` <br/> Sets the Page Size (Or number of items/rows per page) that's fetched from the Database. <br/> Defaults to `20` |
| `Sort`                     | `List<GridSort>` <br/> A list of Fields to sort the Data by. The order of the items in the list is important. It'll be passed to the database in the same order.<br/>  |
| `Filters`                  | `List<GridFilter>` <br/> A list of filters to filter the Data by. |
| `Columns`                  | `List<GridColumn>` <br/> Mainly used to hide fields (Set Visible to false). <br/> Hidden fields are also excluded it in the SQL Query. And if there are table joins, the joining will be omitted.  |
| `Pagination`               | `PaginationConfig` <br/> Adjusts the pagination area.  |
| `ExportConfig`             | `ExportConfig` <br/> Can be used to set the Export flag and the CSV Delimiter.  |



### GridSort
| Property                   | Description                                                                                          |
| ----------------------     | ---------------------------------------------------------------------------------------------------- |
| `Field`                    | `string` <br/> The field (Column) for sorting the Data.  |
| `SortDirection`            | `SortDirection` <br/> An `enum` indicating the direction of the Sort. <br/> `SortDirection.Ascending` or `SortDirection.Descending` |


### GridFilter
We use [`System.Linq.Dynamic.Core`](https://dynamic-linq.net/) under the hood for applying filters.

| Property                   | Description                                                                                          |
| ----------------------     | ---------------------------------------------------------------------------------------------------- |
| `Field`                    | `string` <br/> The field that the filter is applied on.  |
| `Operator`                 | `string` <br/> The filter operator. Can be one of the below:<br/> `=`, `!=`, `>`, `>=`, `<`, `<=`, `Contains`, `In`, `NotIn`, `StartsWith`, `EndsWith` |
| `Value`                    | `object` <br/> The value for filtering (or the search term). |
| `OR`                       | `List<GridFilter>` <br/> A list of `GridFilter` that will be `OR`ed with the crreunt filter. |

### GridColumn
| Property                   | Description                                                                                          |
| ----------------------     | ---------------------------------------------------------------------------------------------------- |
| `HeaderText`               | `string` <br/> The optional Header Text (or Display Text) for the Column. Useful to pass it down to the client from the Server. <br/> |
| `Field`                    | `string` <br/> The Field Name as specified on the LINQ `Select` statement. |
| `Visible`                  | `bool` <br/> When set to `false`, the field will be excluded in the generated SQL. If the field comes from a table join. The join is also omitted |
| `Order`                    | `int` <br/> The order of the Column on the Data Grid. |


### GridPagination

This is purely there to help the client while setting up the pagination area. You might ignore this and rely on `DataPageIndex`, `DataPageSize`, `DataCount` from the [`Grid`](#grid).   
   
Sometimes, the number of rows might be too large that the pagination area itself should be paginated. See the below as an example:

<style>
.md-button{
    font-size:12px;
    padding:10px 5px !important;
    min-width:45px;
}
</style>

!!! note "Example"

    In this example, there are `1,000` rows, `20` rows are shown per page, and the current active page index is `12`.
    
    <button class="md-button">First Page (1)</button>
    <button class="md-button">< Previous</button>
    <button class="md-button">11</button>
    <button class="md-button">12</button>
    <button class="md-button md-button--primary">13</button>
    <button class="md-button">14</button>
    <button class="md-button">15</button>
    <button class="md-button">Next ></button>
    <button class="md-button">Last Page (50)</button>

    Showing [241 to 260] from [1,000]

Below are the properties of the `GridPagination` according to the example.

| Property                   | Description                                                                                          |
| ----------------------     | ---------------------------------------------------------------------------------------------------- |
| `Count`                    | `int` <br/> Number of Pages. In the above example, there are `1,000` rows and the page size is `20`. So the `Count` is `50`. |
| `PageSize`                 | `int` <br/> How many items (Buttons or Links) are shown per page (In the pagination area). <br/> In the above example, the `PageSize` is `5`. <br/> ==Not to be confused with `DataPageSize`== |
| `PageStart`                | `int` <br/> The index of the first page in the current view.. <br/> In the above example, `PageStart` is `10` |
| `PageEnd`                  | `int` <br/> The index of the last page in the current view. <br/> In the above example, `PageEnd` is `14` |
| `PageIndex`                | `int` <br/> The active item (PageIndex). <br/> In the above example, `PageIndex` is `12` |
| `HasPreviousPage`          | `bool` <br/> True when there are more items BEFORE the current page. <br/> In the above example, `HasPreviousPage` is `true` |
| `HasNextPage`              | `bool` <br/> True when there are more items AFTER the current page. <br/> In the above example, `HasNextPage` is `true` |
| `LastPageIndex`            | `int` <br/> The last PageIndex. <br/> In the above example, `LastPageIndex` is `49` |
| `DataStart`                | `int` <br/> The row number (not index) of the first data item. <br/> In the above example, `DataStart` is `241` |
| `DataEnd`                  | `int` <br/> The row number (not index) of the last data item. <br/> In the above example, `DataEnd` is `260` |

### PaginationConfig

| Property                   | Description                                                                                          |
| ----------------------     | ---------------------------------------------------------------------------------------------------- |
| `PageSize`                 | `int` <br/> How many items (Buttons or Links) are shown per page (In the pagination area). <br/> ==Not to be confused with `DataPageSize`== |


### ExportConfig

| Property                   | Description                                                                                          |
| ----------------------     | ---------------------------------------------------------------------------------------------------- |
| `Export`                   | `bool` <br/> The Export Flag. When set to `true`, the data is prepared for export.<br/> We're using the [`FileHelpers`](https://www.filehelpers.net/) for exporting data to CSV  |
| `Delimiter`                | `string` <br/> The Delimiter that's used for seperating data in the exported CSV file/stream. |