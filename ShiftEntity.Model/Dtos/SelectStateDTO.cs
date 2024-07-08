using System;
using System.Collections.Generic;
using System.Text;

namespace ShiftSoftware.ShiftEntity.Model.Dtos;

public class SelectStateDTO<T>
{
    public bool All { get; set; }
    public List<T> Items { get; set; } = [];
    public int Count => All ? Total : Items.Count;
    public int Total { get; set; }
    public string? Filter { get; set; }
}
