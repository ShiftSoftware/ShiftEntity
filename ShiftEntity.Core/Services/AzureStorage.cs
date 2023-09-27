using Azure.Storage.Blobs;
using Azure.Storage.Sas;
using Microsoft.Extensions.Configuration;
using ShiftSoftware.ShiftEntity.Model.Dtos;
using System;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;

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

        value.Url = azureStorageService.GetSignedURL(value.Blob!);

        writer.WriteRawValue(JsonSerializer.Serialize(value));
    }
}