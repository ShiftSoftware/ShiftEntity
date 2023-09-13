using Microsoft.OData.Edm;
using Microsoft.OData.ModelBuilder;

namespace ShiftSoftware.ShiftEntity.Web;

public class ShiftEntityODataOptions
{
    internal bool _Count;
    public ShiftEntityODataOptions Count(bool isCount = true)
    {
        _Count = isCount;
        return this;
    }

    internal bool _Filter;
    public ShiftEntityODataOptions Filter(bool isFilter = true)
    {
        _Filter = isFilter;
        return this;
    }

    internal bool _Expand;
    public ShiftEntityODataOptions Expand(bool isExpand = true)
    {
        _Expand = isExpand;
        return this;
    }

    internal bool _Select;
    public ShiftEntityODataOptions Select(bool isSelect = true)
    {
        _Select = isSelect;
        return this;
    }

    internal bool _OrderBy;
    public ShiftEntityODataOptions OrderBy(bool isOrderBy = true)
    {
        _OrderBy = isOrderBy;
        return this;
    }

    internal int _MaxTop;
    public ShiftEntityODataOptions SetMaxTop(int isMaxTop = 1000)
    {
        _MaxTop = isMaxTop;
        return this;
    }

    public ODataConventionModelBuilder ODataConvention { get; private set; }
    public IEdmModel? EdmModel { get;private set; }
    public string? RoutePrefix { get; set; }


    /// <summary>
    /// This default configure Count, Filter, Expand, Select, OrderBy, MaxTop(1000) and RoutePrefix = "odata"
    /// </summary>
    /// <returns></returns>
    public ShiftEntityODataOptions DefaultOptions()
    {
        this.Count().Filter().Expand().Select().OrderBy().SetMaxTop();
        this.RoutePrefix = "odata";

        return this;
    }

    public ShiftEntityODataOptions()
    {
        this.SetMaxTop();
        ODataConvention = new();
    }

    internal void GenerateEdmModel()
    {
        this.EdmModel = ODataConvention.GetEdmModel();
    }

    public ShiftEntityODataOptions OdataEntitySet<T>(string name) where T : class
    {
        this.ODataConvention.EntitySet<T>(name);

        return this;
    }
}
