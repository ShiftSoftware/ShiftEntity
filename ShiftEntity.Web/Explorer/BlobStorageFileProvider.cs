using Azure;
using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;
using Azure.Storage.Blobs.Specialized;
using Azure.Storage.Sas;
using FastReport.Utils;
using Microsoft.Azure.Cosmos;
using Microsoft.Azure.Cosmos.Linq;
using Microsoft.Extensions.Options;
using ShiftSoftware.ShiftEntity.Core;
using ShiftSoftware.ShiftEntity.Core.Extensions;
using ShiftSoftware.ShiftEntity.Core.Services;
using ShiftSoftware.ShiftEntity.Model;
using ShiftSoftware.ShiftEntity.Model.Dtos;
using ShiftSoftware.ShiftEntity.Model.Enums;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Web;

namespace ShiftSoftware.ShiftEntity.Web.Explorer;

public class BlobStorageFileProvider : IFileProvider
{
    private readonly AzureStorageService azureStorageService;
    private readonly BlobContainerClient container;
    private readonly AzureStorageOption storageOption;
    private readonly IdentityClaimProvider identityClaimProvider;
    private readonly Container? cosmosContainer;

    public BlobStorageFileProvider(AzureStorageService azureStorageService,
        IOptions<FileExplorerConfiguration> config,
        IdentityClaimProvider identityClaimProvider,
        CosmosClient? cosmosClient = null)
    {
        this.azureStorageService = azureStorageService;
        container = azureStorageService.GetBlobContainerClient();

        storageOption = azureStorageService.GetStorageOption();
        this.identityClaimProvider = identityClaimProvider;

        if (storageOption?.SupportsFileExplorer != true)
        {
            throw new Exception($"FileExplorer not supported for storage account ({storageOption?.AccountName})");
        }

        try
        {
            if (cosmosClient != null && config.Value != null && !string.IsNullOrWhiteSpace(config.Value.DatabaseId) && !string.IsNullOrWhiteSpace(config.Value.ContainerId))
            {
                this.cosmosContainer = cosmosClient.GetContainer(config.Value.DatabaseId, config.Value.ContainerId);
            }
        }
        catch { }
    }

    public char Delimiter => '/';
    private IEnumerable<string> ImageExtensions = new List<string>
        {
            ".jpg",
            ".jpeg",
            ".png",
            ".png",
            ".webp",
        };
    private string? GetName(string path) => path.Split(Delimiter, StringSplitOptions.RemoveEmptyEntries).LastOrDefault();

    private async Task<(List<string> list, BlobClient blob)> GetDeletedItems(string path)
    {
        var deletedFilesBlob = container.GetBlobClient(path.AddUrlPath(Constants.FileExplorerHiddenFilename));
        //var test = deletedFilesBlob.AsBlockBlobClient();

        var deletedList = new List<string>();

        if (await deletedFilesBlob.ExistsAsync())
        {
            using BlobDownloadInfo file = await deletedFilesBlob.DownloadAsync();
            using var reader = new StreamReader(file.Content, Encoding.UTF8);
            deletedList = (await reader.ReadToEndAsync())
                .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .ToList();
        }

        return (deletedList, deletedFilesBlob);
    }

    private async Task EnsurePathNotDeletedAsync(string path)
    {
        var pathParts = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (pathParts.Length == 0)
            return;

        var deletedItems = new List<string>();
        var currentPath = "";

        foreach (var part in pathParts)
        {
            var (list, _) = await GetDeletedItems(currentPath);
            deletedItems.AddRange(list);
            currentPath += part + "/";
        }

        var _path = "/" + path;

        if (deletedItems.Any(item => _path.StartsWith(item, StringComparison.OrdinalIgnoreCase)))
        {
            throw new Exception("Directory not found");
        }
    }

    private bool IsPathDirectory(string path)
    {
        if (path == null || path.StartsWith("/") || (!string.IsNullOrWhiteSpace(path) && !path.EndsWith("/")))
            return false;

        return true;
    }

    private async Task<bool> PathExists(string path)
    {
        var exists = false;
        await foreach (var blobItem in container.GetBlobsAsync(prefix: path))
        {
            exists = true;
            break;
        }

        return exists;
    }

    public async Task<FileExplorerResponseDTO> GetFiles(string path, bool includeDeleted = false)
    {
        var res = new FileExplorerResponseDTO(path);

        if (!IsPathDirectory(path))
        {
            res.Message = new Message("Invalid path");
            return res;
        }

        if (!await PathExists(path))
        {
            res.Message = new Message("Directory not found");
            res.Success = false;
            return res;
        }

        // - (?)have an option for recursivly getting file sizes of the folder
        // - (?)support pagination
        // - check access permissions for the user
        // - use Hashset for deleted items to speed up lookups
        // - add support for changing root path
        // - more consistancy when it comes to the path delimiter

        try
        {
            if (!includeDeleted)
                await EnsurePathNotDeletedAsync(path);
        }
        catch (Exception e)
        {
            res.Message = new Message(e.Message);
            return res;
        }

        //var name = GetName(path);
        var files = new List<FileExplorerItemDTO>();
        var deletedItems = await GetDeletedItems(path);

        Console.WriteLine($"Current path: {path} | {path.Length}");

        var pages = container.GetBlobsByHierarchy(BlobTraits.Metadata, delimiter: Delimiter.ToString(), prefix: path).AsPages();

        foreach (Page<BlobHierarchyItem> page in pages)
        {
            foreach (BlobHierarchyItem item in page.Values)
            {
                var entry = new FileExplorerItemDTO { };
                var itemPath = item.IsBlob ? item.Blob.Name : item.Prefix;

                // check if the item is "deleted"
                // if it is and we are not viewing deleted items, skip it
                // otherwise mark it as deleted so the UI can show it as such
                if (deletedItems.list.Contains(Delimiter + itemPath))
                {
                    if (!includeDeleted)
                        continue;

                    entry.IsDeleted = true;
                }

                if (item.IsBlob)
                {
                    var isHiddenEmptyFile = item.Blob.Name.EndsWith(Constants.FileExplorerHiddenFilename);
                    var isHidden = item.Blob.Metadata.TryGetValue(Constants.FileExplorerHiddenMetadataKey, out _);
                    if (isHiddenEmptyFile || isHidden)
                        continue;

                    item.Blob.Metadata.TryGetValue(Constants.FileExplorerNameMetadataKey, out string? originalName);
                    item.Blob.Metadata.TryGetValue(Constants.FileExplorerCreatedByMetadataKey, out string? userId);

                    entry.Name = HttpUtility.UrlDecode(originalName) ?? GetName(item.Blob.Name);
                    entry.Path = item.Blob.Name;
                    entry.Type = Path.GetExtension(entry.Name)?.ToLower();
                    entry.IsFile = true;
                    entry.Size = item.Blob.Properties.ContentLength ?? 0;
                    entry.DateModified = item.Blob.Properties.LastModified?.LocalDateTime ?? default;
                    entry.CreatedBy = userId;

                    entry.Url = azureStorageService.GetSignedURL(item.Blob.Name, BlobSasPermissions.Read, container.Name);

                    if (ImageExtensions.Contains(entry.Type))
                    {
                        item.Blob.Metadata.TryGetValue(Constants.FileExplorerSizesMetadataKey, out string? thumbnailSizes);
                        var prefix = $"{container.AccountName}_{container.Name}";
                        var size = thumbnailSizes?.Split("|").First() ?? "250x250";
                        // GetFileNameWithoutExtension also removes the dir
                        var dir = Path.GetDirectoryName(item.Blob.Name)?.TrimEnd(Delimiter);
                        var name = Path.GetFileNameWithoutExtension(item.Blob.Name);
                        var blobName = $"{dir}/{name}_{size}.png";
                        var thumbnailBlob = prefix.AddUrlPath(blobName);
                        entry.ThumbnailUrl = azureStorageService.GetSignedURL(thumbnailBlob, BlobSasPermissions.Read, storageOption.ThumbnailContainerName, storageOption.AccountName);
                    }
                }
                else if (item.IsPrefix)
                {
                    entry.Name = GetName(item.Prefix);
                    entry.Type = "Directory";
                    entry.Path = item.Prefix;
                }

                files.Add(entry);
            }
        }


        res.Items = files;
        res.Success = true;

        return res;
    }

    public string Combine(params string[] parts)
    {
        if (parts.Length < 2)
        {
            return parts[0];
        }

        var path = string.Join(Delimiter, parts.Select(x => x.Trim(Delimiter)));

        Console.WriteLine($"Combined path: {path}");

        if (parts[0].StartsWith(Delimiter))
            path = Delimiter + path;

        if (parts.Last().EndsWith(Delimiter))
            path = path + Delimiter;

        Console.WriteLine($"Combined path 2: {path}");

        return path;
    }

    public string EnsureEnding(string path)
    {
        return path.EndsWith(Delimiter) ? path : path + Delimiter;
    }

    public string EnsureStart(string path)
    {
        return path.StartsWith(Delimiter) ? path : Delimiter + path;
    }

    public async Task<FileExplorerResponseDTO> Create(string path)
    {
        // (?)check if path already exists by checking the path or checking the special file we create in each folder
        // (?)if it exists, either add a number to the end of the folder name or return an error and from the UI ask if they want to create a new folder
        // or maybe add some hash to the folder name but don't show it in the UI

        var res = new FileExplorerResponseDTO(path);

        if (!IsPathDirectory(path))
        {
            res.Message = new Message("Invalid path");
            return res;
        }

        if (await PathExists(path))
        {
            path = path.TrimEnd(Delimiter) + $"[[{Guid.NewGuid()}]]" + Delimiter;
        }

        BlobClient blob = container.GetBlobClient(path + Constants.FileExplorerHiddenFilename);
        await blob.UploadAsync(new MemoryStream(), overwrite: false);

        await CreateLogItem(path, FileExplorerAction.Create);
        res.Success = true;
        return res;
    }

    public async Task<FileExplorerResponseDTO> Delete(string[] paths)
    {
        var res = new FileExplorerResponseDTO();

        await QueryDeletedItems(paths, static (path, list) =>
        {
            Console.WriteLine($"Deleting {path}");
            if (!list.Contains(path))
                list.Add(path);
            return ValueTask.CompletedTask;
        });

        foreach (var path in paths)
            await CreateLogItem(path, FileExplorerAction.Delete);
        res.Success = true;
        return res;
    }

    public async Task<FileExplorerResponseDTO> Restore(string[] paths)
    {
        var res = new FileExplorerResponseDTO();

        await QueryDeletedItems(paths, static (path, list) =>
        {
            list.RemoveAll(x => x == path);
            return ValueTask.CompletedTask;
        });

        foreach (var path in paths)
            await CreateLogItem(path, FileExplorerAction.Restore);
        res.Success = true;
        return res;
    }

    private async Task QueryDeletedItems(string[] paths, Func<string, List<string>, ValueTask> callback)
    {
        var groupList = paths.Select(static path =>
        {
            var paths = path.Split('/', StringSplitOptions.RemoveEmptyEntries).ToList();
            var name = paths.Last();
            paths.RemoveAt(paths.Count - 1);

            return new
            {
                name,
                IsFile = !path.EndsWith('/'),
                path = string.Join('/', paths),
            };
        }).GroupBy(x => x.path);

        foreach (var group in groupList)
        {
            var path = group.Key;
            var (deletedList, blobClient) = await GetDeletedItems(path);

            foreach (var item in group)
            {
                var fullPath = EnsureStart(Combine(path, item.name));
                if (!item.IsFile)
                    fullPath = EnsureEnding(fullPath);

                await callback.Invoke(fullPath, deletedList);
            }

            using (var stream = await blobClient.OpenWriteAsync(overwrite: true))
            {
                var newContent = string.Join("\n", deletedList) + "\n";
                byte[] newContentBytes = Encoding.UTF8.GetBytes(newContent);
                await stream.WriteAsync(newContentBytes, 0, newContentBytes.Length);
            }
        }
    }

    private async Task CreateLogItem(string path, FileExplorerAction action)
    {
        if (cosmosContainer == null)
        {
            return;
        }

        var log = new LogItem
        {
            Id = Guid.NewGuid().ToString(),
            Action = action.ToString(),
            Path = path,
            Timestamp = DateTime.Now,
            AccountName = container.AccountName,
            Container = container.Name,
            CompanyID = identityClaimProvider.GetCompanyID(),
            CompanyHashedID = identityClaimProvider.GetHashedCompanyID(),
            CompanyBranchID = identityClaimProvider.GetCompanyBranchID(),
            CompanyBranchHashedID = identityClaimProvider.GetHashedCompanyBranchID(),
            UserID = identityClaimProvider.GetUserID(),
            UserHashedID = identityClaimProvider.GetHashedUserID(),
        };

        var partKey = new PartitionKeyBuilder().Add(log.Path).Add(log.Action).Build();
        await cosmosContainer.CreateItemAsync(log, partKey);
    }

    public Task<FileExplorerResponseDTO> Copy(string[] names, string targetPath)
    {
        throw new NotImplementedException();
    }

    public Task<FileExplorerResponseDTO> Details(string path)
    {
        throw new NotImplementedException();
    }

    public Task<FileExplorerResponseDTO> Move(string[] names, string targetPath)
    {
        throw new NotImplementedException();
    }

    public Task<FileExplorerResponseDTO> Rename(string path, string newName)
    {
        throw new NotImplementedException();
    }

    public Task<FileExplorerResponseDTO> Search(string searchString, string path)
    {
        throw new NotImplementedException();
    }
}
