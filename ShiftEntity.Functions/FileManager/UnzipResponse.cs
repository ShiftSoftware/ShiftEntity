using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.Azure.Functions.Worker;
using ShiftSoftware.ShiftEntity.Model.Dtos;

namespace ShiftSoftware.ShiftEntity.Functions.FileManager;

public class UnzipResponse
{
    [QueueOutput(Queues.Unzip)]
    public ZipOptionsDTO[] Messages { get; set; }
    public HttpResponseData HttpResponse { get; set; }
}
