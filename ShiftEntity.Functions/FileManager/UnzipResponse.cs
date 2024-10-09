using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.Azure.Functions.Worker;

namespace ShiftSoftware.ShiftEntity.Functions.FileManager;

public class UnzipResponse
{
    [QueueOutput(Queues.Unzip)]
    public ZipMessages[] Messages { get; set; }
    public HttpResponseData HttpResponse { get; set; }
}
