using Azure.Storage.Sas;
using Microsoft.AspNetCore.Mvc;
using ShiftSoftware.ShiftEntity.Core;
using ShiftSoftware.ShiftEntity.Core.Services;
using ShiftSoftware.ShiftEntity.Model;
using ShiftSoftware.ShiftEntity.Model.Dtos;
using ShiftSoftware.ShiftEntity.Web.Services;
using ShiftSoftware.TypeAuth.AspNetCore;
using ShiftSoftware.TypeAuth.Core;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System;
using System.IO;
using ShiftSoftware.ShiftEntity.Core.Extensions;

namespace ShiftSoftware.ShiftEntity.Web.Controllers;

[Route("api/[controller]")]
[ApiController]
public class AzureStorageController : ControllerBase
{

    private AzureStorageService azureStorageService;
    private readonly IFileExplorerAccessControl? fileExplorerAccessControl;

    public AzureStorageController(AzureStorageService azureStorageService, IFileExplorerAccessControl? fileExplorerAccessControl = null)
    {
        this.azureStorageService = azureStorageService;
        this.fileExplorerAccessControl = fileExplorerAccessControl;
    }

    [HttpPost("generate-file-upload-sas")]
    [TypeAuth(typeof(AzureStorageActionTree), nameof(AzureStorageActionTree.UploadFiles), Access.Write)]
    public ActionResult<ShiftEntityResponse<List<ShiftFileDTO>>> GenerateFileUploadSAS([FromBody] List<ShiftFileDTO> files)
    {
        if (files.Any(x => string.IsNullOrWhiteSpace(x.Blob)))
            return BadRequest(new ShiftEntityResponse<List<ShiftFileDTO>> { Message = new Message("Bad Request", "Blob is required") });

        var res = new ShiftEntityResponse<List<ShiftFileDTO>>();

        foreach (var file in files)
        {
            var AccountName = file.AccountName ?? azureStorageService.GetDefaultAccountName();
            var ContainerName = file.ContainerName ?? azureStorageService.GetDefaultContainerName(AccountName);
            var ext = Path.GetExtension(file.Blob);
            var dir = Path.GetDirectoryName(file.Blob);
            file.Blob = dir.AddUrlPath(Guid.NewGuid().ToString() + ext);

            file.Url = azureStorageService.GetSignedURL(file.Blob, BlobSasPermissions.Write | BlobSasPermissions.Read, ContainerName, AccountName, 60);
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
}