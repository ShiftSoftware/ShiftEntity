﻿using Azure.Storage.Sas;
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
            var blobName = file.Blob;

            try
            {
                var client = azureStorageService.blobServiceClients[AccountName];
                var container = client.GetBlobContainerClient(ContainerName);
                var blob = container.GetBlobClient(file.Blob);

                if (blob.Exists())
                {
                    var ext = System.IO.Path.GetExtension(file.Blob);
                    var blobNameWithoutExtension = string.IsNullOrWhiteSpace(ext) ? file.Blob : file.Blob?.Replace(ext, "");
                    //blobName = file.Blob = $"{blobNameWithoutExtension} ({Guid.NewGuid().ToString().Substring(0, 4)}){ext}";

                    //4 characters is not enough to guarantee uniqueness, there are cases where users upload files with the same name over and over again
                    //Previoully, all Blob names where unique GUIDs, but this was changed to keep the original name for the File Explorer.
                    //It's better that this is changed to an option where each uploader component can be configured for the desired behavior.
                    blobName = file.Blob = $"{blobNameWithoutExtension} ({Guid.NewGuid().ToString()}){ext}";
                }
            }
            catch (Exception) { }

            file.Url = azureStorageService.GetSignedURL(blobName!, BlobSasPermissions.Write | BlobSasPermissions.Read, ContainerName, AccountName, 60);
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