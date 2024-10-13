using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.Text;

namespace ShiftSoftware.ShiftEntity.Model.Dtos;

public class ZipOptionsDTO
{
    public string? ContainerName { get; set; }
    public string Path { get; set; }
    public IEnumerable<string> Names { get; set; }

}
