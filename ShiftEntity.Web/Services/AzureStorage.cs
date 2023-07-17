﻿using Azure.Storage.Blobs;
using Azure.Storage.Sas;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.StaticFiles;
using Microsoft.Extensions.Configuration;
using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;

namespace ShiftSoftware.ShiftEntity.Web.Services
{
    public class AzureStorageService
    {
        private BlobServiceClient blobServiceClient;

        private string? containerName;
        private string? accountName;
        private string? endPoint;
        private string? accountKey;

        IConfiguration configuration;

        public AzureStorageService(IConfiguration configuration)
        {
            this.configuration = configuration;

            this.blobServiceClient = blobServiceClient = new BlobServiceClient(this.configuration.GetValue<string>("AzureStorage:ConnectionString"));

            this.containerName = this.configuration.GetValue<string>("AzureStorage:ContainerName");
            this.accountName = this.configuration.GetValue<string>("AzureStorage:AccountName");
            this.endPoint = this.configuration.GetValue<string>("AzureStorage:EndPoint");
            this.accountKey = this.configuration.GetValue<string>("AzureStorage:AccountKey");
        }

        public async Task<string?> UploadAsync(IFormFile file)
        {
            if (file.Length == 0)
            {
                return null;
            }

            var blobContainerClient = blobServiceClient.GetBlobContainerClient(this.containerName);

            var blobName = $"{Guid.NewGuid()}{Path.GetExtension(file.FileName)}";

            Stream s = new MemoryStream();
            await file.CopyToAsync(s);
            string mimeType = GetMimeTypeForFileExtension(file.FileName);
            s.Seek(0, SeekOrigin.Begin);
            blobContainerClient.GetBlobClient(blobName).Upload(s, new Azure.Storage.Blobs.Models.BlobHttpHeaders { ContentType = mimeType });
            s.Close();

            return blobName;
        }

        public static string GetServiceSasUriForBlob(string blobName, string containerName, string accountName, string accountKey, int expireAfter_Seconds = 3600)
        {
            var sasBuilder = new BlobSasBuilder()
            {
                BlobContainerName = containerName,
                BlobName = blobName,
                Resource = "b",
                ExpiresOn = DateTimeOffset.UtcNow.AddSeconds(expireAfter_Seconds),
            };

            sasBuilder.SetPermissions(BlobSasPermissions.Read);

            var sharedKeyCredential = new Azure.Storage.StorageSharedKeyCredential(accountName, accountKey);

            return sasBuilder.ToSasQueryParameters(sharedKeyCredential).ToString();
        }

        public string GetMimeTypeForFileExtension(string filePath)
        {
            const string DefaultContentType = "application/octet-stream";

            var provider = new FileExtensionContentTypeProvider();

            if (!provider.TryGetContentType(filePath, out string? contentType))
            {
                contentType = DefaultContentType;
            }

            return contentType;
        }

        public string GetSignedURL(string blobName)
        {
            return $"{endPoint}/{containerName}/{blobName}?{GetServiceSasUriForBlob(blobName, containerName!, accountName!, accountKey!)}";
        }
    }
}