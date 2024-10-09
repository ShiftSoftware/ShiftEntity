using Azure.Storage.Blobs;
using Azure.Storage.Sas;
using ShiftSoftware.ShiftEntity.Model.Dtos;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Tasks;

namespace ShiftSoftware.ShiftEntity.Core.Services;

public class AzureStorageOption
{
    public string ConnectionString { get; set; } = default!;
    public string AccountName { get; set; } = default!;
    public string AccountKey { get; set; } = default!;
    public string EndPoint { get; set; } = default!;
    public string DefaultContainerName { get; set; } = default!;
    public bool IsDefaultAccount { get; set; }
}

public class AzureStorageService
{
    internal Dictionary<string, BlobServiceClient> blobServiceClients = new Dictionary<string, BlobServiceClient>();

    internal Dictionary<string, AzureStorageOption> azureStorageAccounts = new Dictionary<string, AzureStorageOption>();

    internal string defaultAccountName = default!;

    public AzureStorageService(List<AzureStorageOption> azureStorageOption)
    {
        foreach (var item in azureStorageOption)
        {
            blobServiceClients[item.AccountName] = new BlobServiceClient(item.ConnectionString);

            azureStorageAccounts[item.AccountName] = item;
        }

        if (azureStorageOption.Count() == 1)
            this.defaultAccountName = azureStorageOption[0].AccountName;

        else if (azureStorageOption.Count() > 1)
        {
            var defaultAccountNames = azureStorageOption.Where(x => x.IsDefaultAccount).Select(x => x.AccountName);

            if (defaultAccountNames.Count() == 1)
                this.defaultAccountName = defaultAccountNames.First();
            else if (defaultAccountNames.Count() == 0)
                throw new Exception("No Azure Storage Account is marked as default.");
            else if (defaultAccountNames.Count() > 1)
                throw new Exception("Only 1 (ONE) Azure Storage Account should be marked as default.");
        }
    }

    public async Task<string?> UploadAsync(string fileName, Stream stream, string? containerName = null, string? accountName = null)
    {
        if (stream.Length == 0)
        {
            return null;
        }

        accountName = accountName ?? defaultAccountName;

        var account = azureStorageAccounts[accountName];
        var blobServiceClient = blobServiceClients[accountName];

        if (containerName == null)
            containerName = account.DefaultContainerName;

        var blobContainerClient = blobServiceClient.GetBlobContainerClient(containerName);

        await blobContainerClient.CreateIfNotExistsAsync();

        var blobName = $"{Guid.NewGuid()}{Path.GetExtension(fileName)}";

        // setting the mimeType to application/octet-stream let's the browser download the file when requesting the url
        string mimeType = "application/octet-stream";

        stream.Seek(0, SeekOrigin.Begin);

        await blobContainerClient.GetBlobClient(blobName).UploadAsync(stream, new Azure.Storage.Blobs.Models.BlobHttpHeaders { ContentType = mimeType });

        stream.Close();

        return blobName;
    }

    public static string GetServiceSasUriForBlob(string blobName, BlobSasPermissions blobSasPermissions, string containerName, string accountName, string accountKey, int expireAfter_Seconds = 3600)
    {
        var sasBuilder = new BlobSasBuilder()
        {
            BlobContainerName = containerName,
            BlobName = blobName,
            Resource = "b",
            ExpiresOn = DateTimeOffset.UtcNow.AddSeconds(expireAfter_Seconds),
        };

        sasBuilder.SetPermissions(blobSasPermissions);

        var sharedKeyCredential = new Azure.Storage.StorageSharedKeyCredential(accountName, accountKey);

        return sasBuilder.ToSasQueryParameters(sharedKeyCredential).ToString();
    }

    public string GetSignedURL(string blobName, BlobSasPermissions blobSasPermissions, string? containerName = null, string? accountName = null, int expireAfter_Seconds = 3600)
    {
        accountName = accountName ?? defaultAccountName;

        var account = azureStorageAccounts[accountName];

        if (containerName == null)
            containerName = account.DefaultContainerName;

        return $"{account.EndPoint}/{containerName}/{blobName}?{GetServiceSasUriForBlob(blobName, blobSasPermissions, containerName!, account.AccountName!, account.AccountKey!, expireAfter_Seconds)}";
    }

    public string GetDefaultAccountName()
    {
        return defaultAccountName;
    }

    public string GetDefaultContainerName(string accountName)
    {
        return azureStorageAccounts[accountName].DefaultContainerName;
    }

    public BlobServiceClient GetBlobServiceClient(string? accountName = null)
    {
        accountName = accountName ?? defaultAccountName;
        return blobServiceClients[accountName];
    }
}


class JsonShiftFileDTOConverter : JsonConverter<ShiftFileDTO>
{
    private readonly AzureStorageService azureStorageService;

    public JsonShiftFileDTOConverter(AzureStorageService azureStorageService)
    {
        this.azureStorageService = azureStorageService;
    }

    public override ShiftFileDTO Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType != JsonTokenType.StartObject)
        {
            throw new JsonException();
        }

        var dto = new ShiftFileDTO();

        while (reader.Read())
        {
            if (reader.TokenType == JsonTokenType.EndObject)
            {
                return dto;
            }

            // Get the key.
            if (reader.TokenType != JsonTokenType.PropertyName)
            {
                throw new JsonException();
            }

            string? propertyName = reader.GetString();

            // Get the value.
            reader.Read();

            var propertyInfo = typeof(ShiftFileDTO).GetProperty(propertyName, BindingFlags.IgnoreCase | BindingFlags.Public | BindingFlags.Instance);

            if (propertyInfo != null)
            {
                var propertyType = propertyInfo.PropertyType;

                var propertyValue = JsonSerializer.Deserialize(ref reader, propertyType, options);

                propertyInfo.SetValue(dto, propertyValue);
            }

            dto.Url = null;
        }

        throw new JsonException();
    }

    public override void Write(Utf8JsonWriter writer, ShiftFileDTO value, JsonSerializerOptions options)
    {
        if (value == default)
        {
            return;
        }

        value.Url = azureStorageService.GetSignedURL(value.Blob!, BlobSasPermissions.Read, value.ContainerName, value.AccountName);

        writer.WriteRawValue(JsonSerializer.Serialize(value));
    }
}