using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;
using Azure.Storage.Blobs.Specialized;
using ShiftSoftware.ShiftEntity.Core.Services;
using System.ComponentModel;
using System.IO.Compression;
using System.Threading;

namespace ShiftSoftware.ShiftEntity.Functions.FileManager;

public class FMM
{
    private AzureStorageService azureStorageService;
    //private BlobServiceClient AzureBlobClient;
    private CompressionLevel Level = CompressionLevel.NoCompression;


    public FMM(AzureStorageService azureStorageService)
    {
        this.azureStorageService = azureStorageService;

        var accountName = azureStorageService.GetDefaultAccountName();
        //AzureBlobClient = azureStorageService.GetBlobServiceClient(accountName);
        //var containerName = azureStorageService.GetDefaultContainerName(accountName);
        //Container = client.GetBlobContainerClient(containerName);

    }

    public async Task ZipFiles(string path, string[] fileNames, CancellationToken cancellationToken)
    {
        var accountName = azureStorageService.GetDefaultAccountName();
        var client = azureStorageService.GetBlobServiceClient(accountName);
        var containerName = azureStorageService.GetDefaultContainerName(accountName);
        var container = client.GetBlobContainerClient(containerName);

        var zipFileName = $"{DateTime.UtcNow:yyyyMMddHHmmss}.{Guid.NewGuid().ToString().Substring(0, 4)}.zip";

        var blob = container.GetBlockBlobClient(path.AddUrlPath(zipFileName));

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

            Console.WriteLine($"Using Level {Level} compression");
            using (var archive = new ZipArchive(zipStream, ZipArchiveMode.Create, false))
            {
                var filesToArchive = new List<string>();

                foreach (var name in fileNames)
                {
                    var filePath = path.AddUrlPath(name);

                    var blockBlobClient = container.GetBlockBlobClient(filePath);
                    if (blockBlobClient.Exists())
                    {
                        filesToArchive.Add(blockBlobClient.Name);
                    }
                    else
                    {
                        // if the blob can't be found then assume it is a virtual directory
                        filesToArchive.AddRange(GetFilesInDir(filePath + "/"));
                    }
                }
                
                foreach (var filePath in filesToArchive)
                {
                    var blockBlobClient = container.GetBlockBlobClient(filePath);
                    var exists = blockBlobClient.Exists();
                    var entry = archive.CreateEntry(blockBlobClient.Name.Replace(path, ""), Level);
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

    private void AddFileToArchive(BlockBlobClient blob, ref ZipArchive archive, CancellationToken cancellationToken)
    {
        var entry = archive.CreateEntry(blob.Name, Level);
        using var stream = entry.Open();
        blob.DownloadTo(stream, cancellationToken);
        //await blob.DownloadToAsync(stream, cancellationToken);
    }

    private List<string> GetFilesInDir(string path)
    {
        var accountName = azureStorageService.GetDefaultAccountName();
        var client = azureStorageService.GetBlobServiceClient(accountName);
        var containerName = azureStorageService.GetDefaultContainerName(accountName);
        var container = client.GetBlobContainerClient(containerName);

        var files = container.GetBlobsByHierarchy(BlobTraits.None, BlobStates.None, "/", path);
        var filePaths = new List<string>();

        foreach (var file in files)
        {
            if (file.IsBlob && !file.Blob.Name.EndsWith("/About.txt"))
            {
                filePaths.Add(file.Blob.Name);
            }
            else if (file.IsPrefix)
            {
                filePaths.AddRange(GetFilesInDir(file.Prefix));
            }
        }

        return filePaths;
    }

    public async Task UnzipFiles(string path, string file, CancellationToken cancellationToken)
    {
        var accountName = azureStorageService.GetDefaultAccountName();
        var client = azureStorageService.GetBlobServiceClient(accountName);
        var containerName = azureStorageService.GetDefaultContainerName(accountName);
        var container = client.GetBlobContainerClient(containerName);

        var blob = container.GetBlockBlobClient(path.AddUrlPath(file));
        var blobsCreated = new List<BlockBlobClient>();

        var zipStream = await blob.OpenReadAsync(cancellationToken: cancellationToken);
        using var archive = new ZipArchive(zipStream, ZipArchiveMode.Read, false);
        
        try
        {
            foreach (var entry in archive.Entries)
            {
                var blockBlobClient = container.GetBlockBlobClient(path.AddUrlPath(entry.FullName));
                blobsCreated.Add(blockBlobClient);

                var blobStream = await blockBlobClient.OpenWriteAsync(true, options: new BlockBlobOpenWriteOptions
                {
                    HttpHeaders = new BlobHttpHeaders
                    {
                        ContentType = MimeTypes.GetMimeType(entry.Name),
                    },
                }, cancellationToken: cancellationToken);

                using var entryStream = entry.Open();
                await entryStream.CopyToAsync(blobStream);
                await blobStream.FlushAsync();
                blobStream.Close();

            }
            cancellationToken.ThrowIfCancellationRequested();
        }
        catch (Exception)
        {
            blobsCreated.ForEach(async x => await x.DeleteIfExistsAsync(cancellationToken: CancellationToken.None));
            throw;
        }

        zipStream.Dispose();
    }

    
}
