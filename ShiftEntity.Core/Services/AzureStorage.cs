using Azure.Storage.Blobs;
using Azure.Storage.Sas;
using Microsoft.Extensions.Configuration;
using System;


namespace ShiftSoftware.ShiftEntity.Core.Services;

public class AzureStorageService
{
    internal BlobServiceClient blobServiceClient;

    internal string? containerName;
    internal string? accountName;
    internal string? endPoint;
    internal string? accountKey;

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

    public string GetSignedURL(string blobName)
    {
        return $"{endPoint}/{containerName}/{blobName}?{GetServiceSasUriForBlob(blobName, containerName!, accountName!, accountKey!)}";
    }
}
