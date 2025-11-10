namespace ShiftSoftware.ShiftEntity.Model.Dtos;

public class ODataDTO<TValue>
{
    public long? Count { get; set; }
    public List<TValue> Value { get; set; } = default!;

    // Uncomment the following line if you need to check converter cache during debugging
    //public Dictionary<string, string?> ConverterCache { get; set; } = new();
}
