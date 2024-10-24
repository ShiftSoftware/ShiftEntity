using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;
using Azure.Storage.Blobs.Specialized;
using ShiftSoftware.ShiftEntity.Core;
using ShiftSoftware.ShiftEntity.Core.Extensions;
using ShiftSoftware.ShiftEntity.Core.Services;
using ShiftSoftware.ShiftEntity.Model.Dtos;
using System.IO.Compression;

namespace ShiftSoftware.ShiftEntity.Functions.FileManager;

public class ArchiveOperations
{
    private AzureStorageService azureStorageService;
    private CompressionLevel Level = CompressionLevel.NoCompression;

    public ArchiveOperations(AzureStorageService azureStorageService)
    {
        this.azureStorageService = azureStorageService;
    }

    public async Task ZipFiles(ZipOptionsDTO zipOptions, CancellationToken cancellationToken)
    {
        var container = GetContainer(zipOptions.ContainerName);

        var zipFileName = $"{DateTime.UtcNow:yyyyMMddHHmmss}.{Guid.NewGuid().ToString().Substring(0, 4)}.zip";
        var zipFileFullname = zipOptions.Path.AddUrlPath(zipFileName);
        var blob = container.GetBlockBlobClient(zipFileFullname);

        try
        {
            // create an empty zip file
            var zipStream = await blob.OpenWriteAsync(true, options: new BlockBlobOpenWriteOptions
            {
                HttpHeaders = new BlobHttpHeaders
                {
                    ContentType = "application/zip",
                },
                Metadata = new Dictionary<string, string>()
                {
                    [Constants.FileManagerHiddenMetadataKey] = "true",
                },
            }, cancellationToken: cancellationToken);

            //var stream = DeflateStream.Synchronized(zipStream);

            Console.WriteLine($"Compression level: {Level}");
            using (var archive = new ZipArchive(zipStream, ZipArchiveMode.Create, false))
            {
                var filesToArchive = new List<string>();

                // get all files, including files in sub directories
                foreach (var fileName in zipOptions.Names)
                {
                    var filePath = zipOptions.Path.AddUrlPath(fileName);

                    if (fileName.EndsWith("/"))
                    {
                        var files = container
                            .GetBlobs(BlobTraits.None, BlobStates.None, filePath + "/", cancellationToken)
                            .Select(x => x.Name)
                            .Where(x => !x.EndsWith("/" + Constants.FileManagerHiddenFilename));
                        filesToArchive.AddRange(files);
                    }
                    else
                    {
                        filesToArchive.Add(filePath);
                    }
                }
                
                // add the selected files to the zip file
                foreach (var filePath in filesToArchive)
                {
                    var blockBlobClient = container.GetBlockBlobClient(filePath);
                    var entry = archive.CreateEntry(blockBlobClient.Name.Replace(zipOptions.Path, ""), Level);
                    using var stream = entry.Open();
                    await blockBlobClient.DownloadToAsync(stream, cancellationToken);
                }
            }


            zipStream.Dispose();
            cancellationToken.ThrowIfCancellationRequested();

            blob.SetMetadata(null, cancellationToken: cancellationToken);
        }
        catch (Exception)
        {
            // delete the zip file if the operation fails midway
            var res = await blob.DeleteIfExistsAsync(cancellationToken: CancellationToken.None);
            throw;
        }

    }

    public async Task UnzipFiles(ZipOptionsDTO zipOptions, CancellationToken cancellationToken)
    {
        var container = GetContainer(zipOptions.ContainerName);

        var fileName = zipOptions.Names.First();
        var fileFullName = zipOptions.Path.AddUrlPath(fileName);
        var blob = container.GetBlockBlobClient(fileFullName);

        var zipStream = await blob.OpenReadAsync(cancellationToken: cancellationToken);
        using (var archive = new ZipArchive(zipStream, ZipArchiveMode.Read, false))
        {
            foreach (var entry in archive.Entries)
            {
                // check if entry is a folder then don't create a file for it
                if (entry.FullName.EndsWith("/")) continue;

                // create a file for each entry in the zip file
                var blockBlobClient = container.GetBlockBlobClient(zipOptions.Path.AddUrlPath(entry.FullName));

                using var entryStream = entry.Open();
                blockBlobClient.Upload(entryStream, new BlobUploadOptions
                {
                    HttpHeaders = new BlobHttpHeaders
                    {
                        ContentType = MimeTypes.GetMimeType(entry.Name),
                    },
                }, cancellationToken: cancellationToken);

            }
        }

        zipStream.Dispose();
    }

    private BlobContainerClient GetContainer(string? _containerName)
    {
        var accountName = azureStorageService.GetDefaultAccountName();
        var client = azureStorageService.GetBlobServiceClient(accountName);
        var containerName = _containerName ?? azureStorageService.GetDefaultContainerName(accountName);
        return client.GetBlobContainerClient(containerName);
    }
    
}
