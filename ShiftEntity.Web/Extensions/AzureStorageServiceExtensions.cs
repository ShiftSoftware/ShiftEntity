
using Azure.Storage.Blobs;
using Microsoft.AspNetCore.Http;
using System.IO;
using System.Threading.Tasks;
using System;
using ShiftSoftware.ShiftEntity.Core.Services;

namespace ShiftSoftware.ShiftEntity.Web.Extensions;

public static class AzureStorageServiceExtensions
{
    public static async Task<string?> UploadAsync(this AzureStorageService azureStorageService, IFormFile file)
    {
        if (file.Length == 0)
        {
            return null;
        }

        var blobContainerClient = azureStorageService.blobServiceClient.GetBlobContainerClient(azureStorageService.containerName);

        var blobName = $"{Guid.NewGuid()}{Path.GetExtension(file.FileName)}";

        Stream s = new MemoryStream();
        await file.CopyToAsync(s);
        // setting the mimeType to application/octet-stream let's the browser download the file when requesting the url
        string mimeType = "application/octet-stream";
        s.Seek(0, SeekOrigin.Begin);
        blobContainerClient.GetBlobClient(blobName).Upload(s, new Azure.Storage.Blobs.Models.BlobHttpHeaders { ContentType = mimeType });
        s.Close();

        return blobName;
    }
}
