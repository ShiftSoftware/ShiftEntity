using Microsoft.OData.ModelBuilder;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

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

    public ODataConventionModelBuilder ODataConvention;
    public string RoutePrefix { get; set; }


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
    }
}
