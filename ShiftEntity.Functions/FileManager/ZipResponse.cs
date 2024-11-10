using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using ShiftSoftware.ShiftEntity.Model.Dtos;

namespace ShiftSoftware.ShiftEntity.Functions.FileExplorer;

public class ZipResponse
{
    [QueueOutput(Queues.Zip)]
    public ZipOptionsDTO[] Messages { get; set; }
    public HttpResponseData HttpResponse { get; set; }
}
