using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;

namespace ShiftSoftware.ShiftEntity.Functions.FileManager;

public class ZipResponse
{
    [QueueOutput(Queues.Zip)]
    public ZipMessages[] Messages { get; set; }
    public HttpResponseData HttpResponse { get; set; }
}
