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
    private readonly FileExplorerConfiguration config;

    public BlobStorageFileProvider(AzureStorageService azureStorageService,
        IOptions<FileExplorerConfiguration> config,
        IdentityClaimProvider identityClaimProvider,
        CosmosClient? cosmosClient = null)
    {
        this.azureStorageService = azureStorageService;
        this.container = azureStorageService.GetBlobContainerClient();
        this.storageOption = azureStorageService.GetStorageOption();
        this.identityClaimProvider = identityClaimProvider;
        this.config = config.Value;

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

    public async Task<FileExplorerResponseDTO> GetFiles(FileExplorerReadDTO data)
    {
        var path = data.Path ?? "";
        var res = new FileExplorerResponseDTO(path);

        if (!IsPathDirectory(path))
        {
            res.Message = new Message("Invalid path");
            return res;
        }

        // - (?)have an option for recursivly getting file sizes of the folder
        // - check access permissions for the user
        // - use Hashset for deleted items to speed up lookups
        // - more consistancy when it comes to the path delimiter
        // - GetFiles could potentially return empty list if the first page only contains deleted files


        // get the full list of items in the first Page
        // if list is empty, then we stop as the dir is empty
        // otherwise we start checking previous dirs to make sure
        // that this current dir is not deleted or is in a deleted dir
        // then we will process the list of items in the Page
        var pages = container.GetBlobsByHierarchy(BlobTraits.Metadata, delimiter: Delimiter.ToString(), prefix: path).AsPages(data.ContinuationToken, config.PageSizeHint);

        var workingPage = pages.First();
        res.ContinuationToken = workingPage.ContinuationToken;

        if (workingPage.Values.Count == 0)
        {
            res.Message = new Message("Directory not found");
            return res;
        }

        try
        {
            if (!data.IncludeDeleted)
                await EnsurePathNotDeletedAsync(path);
        }
        catch (Exception e)
        {
            res.Message = new Message(e.Message);
            return res;
        }

        var files = new List<FileExplorerItemDTO>();
        var deletedItems = await GetDeletedItems(path);

        foreach (BlobHierarchyItem item in workingPage.Values)
        {
            var entry = new FileExplorerItemDTO { };
            var itemPath = item.IsBlob ? item.Blob.Name : item.Prefix;

            // check if the item is "deleted"
            // if it is and we are not viewing deleted items, skip it
            // otherwise mark it as deleted so the UI can show it as such
            if (deletedItems.list.Contains(Delimiter + itemPath))
            {
                if (!data.IncludeDeleted)
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

        if (parts[0].StartsWith(Delimiter))
            path = Delimiter + path;

        if (parts.Last().EndsWith(Delimiter))
            path = path + Delimiter;

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

    public async Task<FileExplorerResponseDTO> Create(FileExplorerCreateDTO data)
    {
        // instead of listing (GetBlobsByHierarchy), keep retrying to upload the file
        // or await foreach instead of .ToList
        // consider validating folder names to work with common operators 

        var path = data.Path;
        var res = new FileExplorerResponseDTO(path);
        (string dir, string? folderName) = PathDetail(path);

        if (path == null || !IsPathDirectory(path) || string.IsNullOrWhiteSpace(folderName))
        {
            res.Message = new Message("Invalid path");
            return res;
        }

        var pages = container.GetBlobsByHierarchy(BlobTraits.Metadata, delimiter: Delimiter.ToString(), prefix: dir).AsPages().ToList();
        var dirs = pages.Select(x => x.Values.Where(x => x.IsPrefix)).SelectMany(x => x);
        // remove whitespaces
        folderName = folderName.Trim();

        var newPath = dir + folderName + "/";
        var count = 1;

        while (dirs.Any(x => x.Prefix == newPath))
        {
            newPath = $"{dir}{folderName} ({count++})/";
        }

        try
        {
            var stream = new MemoryStream();
            BlobClient blob = container.GetBlobClient(newPath + Constants.FileExplorerHiddenFilename);
            await blob.UploadAsync(stream, overwrite: false);
            stream.Dispose();

            await CreateLogItem(newPath, FileExplorerAction.Create);
            res.Success = true;
        }
        catch (Azure.RequestFailedException e)
        {
            if (e.Status == 409)
                res.Message = new Message("Folder already exists");
            else
                res.Message = new Message($"Could not create folder");
        }

        res.Path = newPath;
        return res;
    }

    private (string dir, string? name) PathDetail(string? path)
    {
        var parts = path?.Split(Delimiter, StringSplitOptions.RemoveEmptyEntries).ToList();
        if (parts == null || parts.Count == 0)
            return ("", null);

        var name = parts.Last();
        parts.RemoveAt(parts.Count - 1);
        var dir = string.Join('/', parts);
        dir = string.IsNullOrWhiteSpace(dir) ? "" : dir + "/";

        return (dir, name);
    }

    public async Task<FileExplorerResponseDTO> Delete(FileExplorerDeleteDTO data)
    {
        var res = new FileExplorerResponseDTO();

        await QueryDeletedItems(data.Paths, static (path, list) =>
        {
            if (!list.Contains(path))
                list.Add(path);
            return ValueTask.CompletedTask;
        });

        foreach (var path in data.Paths)
            await CreateLogItem(path, FileExplorerAction.Delete);
        res.Success = true;
        return res;
    }

    public async Task<FileExplorerResponseDTO> Restore(FileExplorerRestoreDTO data)
    {
        var res = new FileExplorerResponseDTO();

        await QueryDeletedItems(data.Paths, static (path, list) =>
        {
            list.RemoveAll(x => x == path);
            return ValueTask.CompletedTask;
        });

        foreach (var path in data.Paths)
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

    public async Task<FileExplorerResponseDTO> Details(FileExplorerDetailDTO data)
    {
        var path = data.Path ?? "";
        var isDir = path.EndsWith("/") || path == string.Empty;
        var res = new FileExplorerResponseDTO(path);

        if (isDir)
        {
            var pages = container.GetBlobs(prefix: path).AsPages(data.ContinuationToken, config.PageSizeHint);
            var workingPage = pages.First();
            res.ContinuationToken = workingPage.ContinuationToken;

            if (workingPage.Values.Count == 0)
            {
                res.Success = true;
                return res;
            }

            try
            {
                if (!data.IncludeDeleted)
                    await EnsurePathNotDeletedAsync(path);
            }
            catch
            {
                res.Success = true;
                return res;
            }


            IEnumerable<string> deletedItems = [];

            if (data.IncludeDeleted)
            {
                deletedItems = workingPage.Values
                    .Where(blob => blob.Name.EndsWith("/" + Constants.FileExplorerHiddenFilename))
                    .Select(blob => GetDeletedItems(blob.Name).Result.list)
                    .SelectMany(x => x);
            }

            var blobs = data.IncludeDeleted
                ? workingPage.Values
                : workingPage.Values.Where(blob => !deletedItems.Contains(blob.Name));

            int count = blobs.Count();
            var totalSize = blobs.Aggregate(0L, (total, blob) => total += blob.Properties.ContentLength ?? 0);

            res.Additional = new
            {
                Count = count,
                TotalSize = totalSize,
            };
            return res;
        }
        else
        {
            return res;
        }
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
