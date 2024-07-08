using System;
using System.Collections.Generic;
using System.Text;

namespace ShiftSoftware.ShiftEntity.Model.Dtos;

public class SelectState<T>
{
    public bool All { get; set; }
    public List<T> Items { get; set; } = [];
    public int Count => All ? Total : Items.Count;
    public int Total { get; set; }
    public string? Filter { get; set; }

    public void Clear()
    {
        Items.Clear();
        All = false;
        Filter = null;
    }
}
