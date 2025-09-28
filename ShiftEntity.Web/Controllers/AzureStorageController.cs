using Azure.Storage.Sas;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Azure.Cosmos;
using Microsoft.Extensions.Options;
using ShiftSoftware.ShiftEntity.Core;
using ShiftSoftware.ShiftEntity.Core.Services;
using ShiftSoftware.ShiftEntity.Model;
using ShiftSoftware.ShiftEntity.Model.Dtos;
using ShiftSoftware.ShiftEntity.Model.Enums;
using ShiftSoftware.ShiftEntity.Web.Services;
using ShiftSoftware.TypeAuth.AspNetCore;
using ShiftSoftware.TypeAuth.Core;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System;
using System.IO;
using System.Threading.Tasks;
using ShiftSoftware.ShiftEntity.Core.Extensions;

namespace ShiftSoftware.ShiftEntity.Web.Controllers;

[Route("api/[controller]")]
[ApiController]
public class AzureStorageController : ControllerBase
{

    private AzureStorageService azureStorageService;
    private readonly IFileExplorerAccessControl? fileExplorerAccessControl;
    private Container? cosmosContainer;
    private IIdentityClaimProvider identityClaimProvider;

    public AzureStorageController(AzureStorageService azureStorageService, IOptions<FileExplorerConfiguration> config, IIdentityClaimProvider identityClaimProvider, IFileExplorerAccessControl? fileExplorerAccessControl = null, CosmosClient? cosmosClient = null)
    {
        this.azureStorageService = azureStorageService;
        this.fileExplorerAccessControl = fileExplorerAccessControl;
        this.identityClaimProvider = identityClaimProvider;

        try
        {
            if (cosmosClient != null && config.Value != null && !string.IsNullOrWhiteSpace(config.Value.DatabaseId) && !string.IsNullOrWhiteSpace(config.Value.ContainerId))
            {
                this.cosmosContainer = cosmosClient.GetContainer(config.Value.DatabaseId, config.Value.ContainerId);
            }
        }
        catch { }
    }

    [HttpPost("generate-file-upload-sas")]
    [TypeAuth(typeof(AzureStorageActionTree), nameof(AzureStorageActionTree.UploadFiles), Access.Write)]
    public async Task<ActionResult<ShiftEntityResponse<List<ShiftFileDTO>>>> GenerateFileUploadSAS([FromBody] List<ShiftFileDTO> files)
    {
        if (files.Any(x => string.IsNullOrWhiteSpace(x.Blob)))
            return BadRequest(new ShiftEntityResponse<List<ShiftFileDTO>> { Message = new Message("Bad Request", "Blob is required") });

        var res = new ShiftEntityResponse<List<ShiftFileDTO>>();

        foreach (var file in files)
        {
            var accountName = file.AccountName ?? azureStorageService.GetDefaultAccountName();
            var containerName = file.ContainerName ?? azureStorageService.GetDefaultContainerName(accountName);
            var ext = Path.GetExtension(file.Blob);
            var dir = Path.GetDirectoryName(file.Blob);

            // Store original file name in the blob. In case it's needed outside of the uploader/explorer component.
            // Add a unique identifier to the blob name to avoid conflicts.
            file.Blob = dir.AddUrlPath($"{Path.GetFileNameWithoutExtension(file.Blob)} ({Guid.NewGuid().ToString()}){ext}");

            file.Url = azureStorageService.GetSignedURL(file.Blob, BlobSasPermissions.Write | BlobSasPermissions.Read, containerName, accountName, 60);

            await CreateLogItem(file.Blob, FileExplorerAction.Create, accountName, containerName);
        }

        if (this.fileExplorerAccessControl is not null)
        {
            files = this.fileExplorerAccessControl.FilterWithWriteAccess(files);
        }

        res.Entity = files;

        return new ContentResult()
        {
            Content = JsonSerializer.Serialize(res, new JsonSerializerOptions { }),
            ContentType = "application/json"
        };
    }

    private async Task CreateLogItem(string path, FileExplorerAction action, string accountName, string containerName)
    {
        if (cosmosContainer == null)
            return;

        var log = new LogItem
        {
            Id = Guid.NewGuid().ToString(),
            Action = FileExplorerAction.Create.ToString(),
            Path = path,
            Timestamp = DateTime.Now,
            AccountName = accountName,
            Container = containerName,
            CompanyID = identityClaimProvider.GetCompanyID(),
            CompanyHashedID = identityClaimProvider.GetHashedCompanyID(),
            CompanyBranchID = identityClaimProvider.GetCompanyBranchID(),
            CompanyBranchHashedID = identityClaimProvider.GetHashedCompanyBranchID(),
            UserID = identityClaimProvider.GetUserID(),
            UserHashedID = identityClaimProvider.GetHashedUserID(),
        };

        var partKey = new PartitionKeyBuilder().Add(log.Path).Add(log.Action).Build();
        await cosmosContainer.CreateItemAsync(log, partKey);
    }
}