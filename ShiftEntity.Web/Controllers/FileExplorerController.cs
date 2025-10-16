using Microsoft.AspNetCore.Mvc;
using Microsoft.Azure.Cosmos;
using Microsoft.Extensions.Options;
using ShiftSoftware.ShiftEntity.Core;
using ShiftSoftware.ShiftEntity.Core.Services;
using ShiftSoftware.ShiftEntity.Model;
using ShiftSoftware.ShiftEntity.Model.Dtos;
using ShiftSoftware.ShiftEntity.Web.Explorer;
using ShiftSoftware.ShiftEntity.Web.Services;
using ShiftSoftware.TypeAuth.AspNetCore;
using ShiftSoftware.TypeAuth.Core;
using System;
using System.Net.Http;
using System.Net.Http.Json;
using System.Threading.Tasks;

namespace ShiftSoftware.ShiftEntity.Web.Controllers;

[Route("api/[controller]")]
[ApiController]
public class FileExplorerController : ControllerBase
{
    private HttpClient httpClient;
    private string? AzureFunctionsEndpoint;
    private IFileProvider fileProvider;

    public FileExplorerController(HttpClient httpClient, IFileProvider? fileProvider, IOptions<FileExplorerConfiguration> config)
    {
        this.httpClient = httpClient;
        this.AzureFunctionsEndpoint = config.Value?.FunctionsEndpoint;
        this.fileProvider = fileProvider;
    }

    [HttpGet]
    [Route("list")]
    [TypeAuth(typeof(AzureStorageActionTree), nameof(AzureStorageActionTree.UploadFiles), Access.Read)]
    public async Task<FileExplorerResponseDTO> List([FromQuery] FileExplorerReadDTO data)
    {
        return await fileProvider.GetFiles(data);
    }

    [HttpPost]
    [Route("create")]
    [TypeAuth(typeof(AzureStorageActionTree), nameof(AzureStorageActionTree.UploadFiles), Access.Write)]
    public async Task<FileExplorerResponseDTO> Create([FromBody] FileExplorerCreateDTO data)
    {
        return await fileProvider.Create(data);
    }

    [HttpPost]
    [Route("delete")]
    [TypeAuth(typeof(AzureStorageActionTree), nameof(AzureStorageActionTree.UploadFiles), Access.Delete)]
    public async Task<FileExplorerResponseDTO> Delete([FromBody] FileExplorerDeleteDTO data)
    {
        return await fileProvider.Delete(data);
    }

    [HttpPost]
    [Route("restore")]
    [TypeAuth(typeof(AzureStorageActionTree), nameof(AzureStorageActionTree.UploadFiles), Access.Delete)]
    public async Task<FileExplorerResponseDTO> Restore([FromBody] FileExplorerRestoreDTO data)
    {
        return await fileProvider.Restore(data);
    }

    [HttpGet]
    [Route("detail")]
    [TypeAuth(typeof(AzureStorageActionTree), nameof(AzureStorageActionTree.UploadFiles), Access.Read)]
    public async Task<FileExplorerResponseDTO> Detail([FromQuery] FileExplorerDetailDTO data)
    {
        return await fileProvider.Detail(data);
    }

    [HttpPost("ZipFiles")]
    public async Task<ActionResult> ZipFiles(ZipOptionsDTO zipOptions)
    {
        if (string.IsNullOrWhiteSpace(AzureFunctionsEndpoint))
        {
            throw new ArgumentNullException("AzureFunctions:Endpoint not found in appsettings.json");
        }
        var res = await httpClient.PostAsJsonAsync(AzureFunctionsEndpoint + "/api/zip", zipOptions);
        return StatusCode((int)res.StatusCode);
    }

    [HttpPost("UnzipFiles")]
    public async Task<ActionResult> UnzipFiles(ZipOptionsDTO zipOptions)
    {
        if (string.IsNullOrWhiteSpace(AzureFunctionsEndpoint))
        {
            throw new ArgumentNullException("AzureFunctions:Endpoint not found in appsettings.json");
        }
        var res = await httpClient.PostAsJsonAsync(AzureFunctionsEndpoint+ "/api/unzip", zipOptions);
        return StatusCode((int)res.StatusCode);
    }

}