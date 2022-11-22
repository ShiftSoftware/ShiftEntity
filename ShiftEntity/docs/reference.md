The Shift Grid fetches data and prepares it in a format that can be easily integrated into a Data Table.

### Grid

The ``Grid`` (``ShiftSoftware.ShiftGrid.Core.Grid``) can be initialized by calling the [ToShiftGridAsync](/methods/#toshiftgridasync-toshiftgrid) or [ToShiftGrid](/methods/#toshiftgridasync-toshiftgrid) extension methods on an ``IQueryable``.  
     
The ``Grid`` contains below properties.

| Property                   | Description                                                                                          |
| ----------------------     | ---------------------------------------------------------------------------------------------------- |
| `DataPageIndex`            | `int` <br/> The current page index of the paginated data. |
| `DataPageSize`             | `int` <br/> The Number of Items (Or number of rows) per Page. |
| `DataCount`                | `int` <br/> The total count of the data (The Unpaginated Count). |
| `Data`                     | `List<T>` <br/> This is the actual data that's fetched from Database.<br/>  |
| `Aggregate`                | `T2` <br/> Aggregated Data. This is available if [SelectAggregate](/methods/#selectaggregate) extension method is used. |
| `Sort`                     | `List<GridSort>` <br/> The list of Fields that the Data is sorted by.<br/>  |
| `StableSort`               | `GridSort` <br/> The mandatory Stable Sort that the data is sorted by.<br/> [Learn more about Stable Sorting](/philosophy/#stable-sort) |
| `Filters`                  | `List<GridFilter>` <br/> The list of filters that the data is filtered by. |
| `Columns`                  | `List<GridColumn>` <br/> The column defnition of the Dataset that contains below:<br/> `HeaderText`, `Field`, `Visible`, and `Order`.  |
| `Pagination`               | `GridPagination` <br/> Information about the pagination area.  |
| `BeforeLoadingData`        | `DateTime` (UTC) <br/> The timestamp just before making the database call(s)  |
| `AfterLoadingData`        | `DateTime` (UTC) <br/> The timestamp just after the data is finished loading from database  |

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