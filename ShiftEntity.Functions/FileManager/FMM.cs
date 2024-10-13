using Azure.Storage.Blobs.Models;
using Azure.Storage.Blobs.Specialized;
using ShiftSoftware.ShiftEntity.Core.Services;
using ShiftSoftware.ShiftEntity.Model.Dtos;
using System.IO.Compression;

namespace ShiftSoftware.ShiftEntity.Functions.FileManager;

public class FMM
{
    private AzureStorageService azureStorageService;
    private CompressionLevel Level = CompressionLevel.NoCompression;

    public FMM(AzureStorageService azureStorageService)
    {
        this.azureStorageService = azureStorageService;
    }

    public async Task ZipFiles(ZipOptionsDTO zipOptions, CancellationToken cancellationToken)
    {
        var accountName = azureStorageService.GetDefaultAccountName();
        var client = azureStorageService.GetBlobServiceClient(accountName);
        var containerName = azureStorageService.GetDefaultContainerName(accountName);
        var container = client.GetBlobContainerClient(containerName);

        var zipFileName = $"{DateTime.UtcNow:yyyyMMddHHmmss}.{Guid.NewGuid().ToString().Substring(0, 4)}.zip";

        var blob = container.GetBlockBlobClient(zipOptions.Path.AddUrlPath(zipFileName));

        try
        {
            var zipStream = await blob.OpenWriteAsync(true, options: new BlockBlobOpenWriteOptions
            {
                HttpHeaders = new BlobHttpHeaders
                {
                    ContentType = "application/zip",
                },
            }, cancellationToken: cancellationToken);

            //var stream = DeflateStream.Synchronized(zipStream);

            Console.WriteLine($"Compression level: {Level}");
            using (var archive = new ZipArchive(zipStream, ZipArchiveMode.Create, false))
            {
                var filesToArchive = new List<string>();

                foreach (var fileName in zipOptions.Names)
                {
                    var filePath = zipOptions.Path.AddUrlPath(fileName);

                    var blockBlobClient = container.GetBlockBlobClient(filePath);
                    if (blockBlobClient.Exists())
                    {
                        filesToArchive.Add(blockBlobClient.Name);
                    }
                    else
                    {
                        // if the blob can't be found then assume it is a virtual directory
                        var files = container
                            .GetBlobs(BlobTraits.None, BlobStates.None, filePath + "/", cancellationToken)
                            .Select(x => x.Name)
                            .Where(x => !x.EndsWith("/About.txt"));
                        filesToArchive.AddRange(files);
                    }
                }
                
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

        }
        catch (Exception)
        {
            var res = await blob.DeleteIfExistsAsync(cancellationToken: CancellationToken.None);
            throw;
        }

    }

    public async Task UnzipFiles(ZipOptionsDTO zipOptions, CancellationToken cancellationToken)
    {
        var accountName = azureStorageService.GetDefaultAccountName();
        var client = azureStorageService.GetBlobServiceClient(accountName);
        var containerName = azureStorageService.GetDefaultContainerName(accountName);
        var container = client.GetBlobContainerClient(containerName);

        var fileName = zipOptions.Names.First();
        var blob = container.GetBlockBlobClient(zipOptions.Path.AddUrlPath(fileName));

        var zipStream = await blob.OpenReadAsync(cancellationToken: cancellationToken);
        using (var archive = new ZipArchive(zipStream, ZipArchiveMode.Read, false))
        {
            foreach (var entry in archive.Entries)
            {
                if (entry.Length == 0) continue;

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
    
}
