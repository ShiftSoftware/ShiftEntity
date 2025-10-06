using Microsoft.AspNetCore.Mvc;
using Microsoft.Azure.Cosmos;
using Microsoft.Extensions.Options;
using ShiftSoftware.ShiftEntity.Core;
using ShiftSoftware.ShiftEntity.Core.Services;
using ShiftSoftware.ShiftEntity.Model;
using ShiftSoftware.ShiftEntity.Model.Dtos;
using ShiftSoftware.ShiftEntity.Web.Explorer;
using ShiftSoftware.ShiftEntity.Web.Services;
using System;
using System.Net.Http;
using System.Net.Http.Json;
using System.Threading.Tasks;

namespace ShiftSoftware.ShiftEntity.Web.Controllers;

[Route("api/[controller]")]
[ApiController]
public class FileExplorerController : ControllerBase
{
    private readonly AzureStorageService azureStorageService;
    private readonly IFileExplorerAccessControl? fileExplorerAccessControl;
    private HttpClient httpClient;
    private string? AzureFunctionsEndpoint;
    private IFileProvider fileProvider;

    public FileExplorerController(AzureStorageService azureStorageService, HttpClient httpClient, IFileProvider? fileProvider, IOptions<FileExplorerConfiguration> config, IdentityClaimProvider identityClaimProvider, CosmosClient? cosmosClient = null, IFileExplorerAccessControl? fileExplorerAccessControl = null)
    {
        //TODO
        // implement access control

        this.httpClient = httpClient;
        this.AzureFunctionsEndpoint = config.Value?.FunctionsEndpoint;
        this.fileExplorerAccessControl = fileExplorerAccessControl;
        this.fileProvider = fileProvider;
    }

    [HttpGet]
    [Route("list")]
    public async Task<FileExplorerResponseDTO> List([FromQuery] FileExplorerReadDTO data)
    {
        return await fileProvider.GetFiles(data.Path ?? "", data.IncludeDeleted);
    }

    [HttpPost]
    [Route("create")]
    public async Task<FileExplorerResponseDTO> Create([FromBody] FileExplorerCreateDTO data)
    {
        return await fileProvider.Create(data.Path);
    }

    [HttpPost]
    [Route("delete")]
    public async Task<FileExplorerResponseDTO> Delete([FromBody] FileExplorerDeleteDTO data)
    {
        return await fileProvider.Delete(data.Paths);
    }

    [HttpPost]
    [Route("restore")]
    public async Task<FileExplorerResponseDTO> Restore([FromBody] FileExplorerDeleteDTO data)
    {
        return await fileProvider.Restore(data.Paths);
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