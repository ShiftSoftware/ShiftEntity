using Azure;
using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;
using Azure.Storage.Sas;
using Microsoft.Azure.Cosmos;
using Microsoft.Extensions.Options;
using ShiftSoftware.ShiftEntity.Core;
using ShiftSoftware.ShiftEntity.Core.Services;
using ShiftSoftware.ShiftEntity.Model;
using ShiftSoftware.ShiftEntity.Model.Dtos;
using ShiftSoftware.ShiftEntity.Model.Enums;
using ShiftSoftware.ShiftEntity.Model.FileExplorer;
using ShiftSoftware.ShiftEntity.Model.FileExplorer.Dtos;
using ShiftSoftware.ShiftEntity.Web.Explorer;
using ShiftSoftware.TypeAuth.Core;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Web;

namespace ShiftSoftware.ShiftEntity.Web.Services;

// TODO?
// - use Hashset for deleted items to speed up lookups
// - GetFiles could potentially return empty list if the first page only contains deleted files

public class BlobStorageFileProvider : IFileProvider
{
    private readonly AzureStorageService azureStorageService;
    private BlobContainerClient container;
    private readonly AzureStorageOption storageOption;
    private readonly IdentityClaimProvider identityClaimProvider;
    private readonly Container? cosmosContainer;
    private readonly FileExplorerConfiguration config;
    private readonly IFileExplorerAccessControl? fileExplorerAccessControl;
    private readonly ITypeAuthService? typeAuthService;

    const int MAX_CREATE_RETRY_ATTEMPTS = 25;

    public BlobStorageFileProvider(AzureStorageService azureStorageService,
        IOptions<FileExplorerConfiguration> config,
        IdentityClaimProvider identityClaimProvider,
        ITypeAuthService typeAuthService,
        IFileExplorerAccessControl? fileExplorerAccessControl = null,
        CosmosClient? cosmosClient = null)
    {
        this.azureStorageService = azureStorageService;        
        storageOption = azureStorageService.GetStorageOption();
        this.identityClaimProvider = identityClaimProvider;
        this.fileExplorerAccessControl = fileExplorerAccessControl;
        this.config = config.Value;
        this.typeAuthService = typeAuthService;

        if (storageOption?.SupportsFileExplorer != true)
        {
            throw new Exception($"FileExplorer not supported for storage account ({storageOption?.AccountName})");
        }

        try
        {
            if (cosmosClient != null && config.Value != null && !string.IsNullOrWhiteSpace(config.Value.DatabaseId) && !string.IsNullOrWhiteSpace(config.Value.ContainerId))
            {
                cosmosContainer = cosmosClient.GetContainer(config.Value.DatabaseId, config.Value.ContainerId);
            }
        }
        catch { }
    }

    public char Delimiter => BlobHelper.Delimiter;
    private static readonly HashSet<string> ImageExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".jpg",
        ".jpeg",
        ".png",
        ".webp",
    };

    private async Task<(List<string> list, BlobClient blob)> GetDeletedItems(string path)
    {
        var hiddenPath = BlobHelper.Combine(path, Constants.FileExplorerHiddenFilename);

        var blob = container.GetBlobClient(hiddenPath);
        var list = new List<string>();

        try
        {
            // instead of using blob.ExistsAsync, we get the Properties of the blob
            // and check for the content length, and if the file doesn't exist, we catch the error
            // both methods make a HEAD request
            var props = await blob.GetPropertiesAsync().ConfigureAwait(false);
            if (props.Value.ContentLength > 0)
            {
                BlobDownloadResult file = await blob.DownloadContentAsync().ConfigureAwait(false);
                var text = file.Content.ToString();
                list = text
                    .Split(['\n', '\r'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                    .ToList() ?? [];
            }
        }
        catch (RequestFailedException e) when (e.Status == 404) { }

        return (list, blob);
    }

    private async Task EnsurePathNotDeletedAsync(string path)
    {
        var pathParts = path?.Split(Delimiter, StringSplitOptions.RemoveEmptyEntries);
        if (pathParts == null || pathParts.Length == 0)
            return;

        //var deletedItems = new List<string>();
        var currentPath = "";
        var fullPath = Delimiter + path;

        foreach (var part in pathParts)
        {
            var (list, _) = await GetDeletedItems(currentPath);
            if (list.Any(p => fullPath.StartsWith(p, StringComparison.Ordinal)))
                throw new DirectoryNotFoundException();

            currentPath += part + Delimiter;
        }
    }

    public async Task<FileExplorerResponseDTO> GetFiles(FileExplorerReadDTO data)
    {
        var path = data.Path ?? "";
        var res = new FileExplorerResponseDTO(path);

        if (!BlobHelper.IsPathDirectory(path))
        {
            res.Message = new Message("Invalid path");
            return res;
        }

        // get the full list of items in the first Page
        // if list is empty, then we stop as the dir is empty
        // otherwise we start checking previous dirs to make sure
        // that this current dir is not deleted or is in a deleted dir
        // then we will process the list of items in the Page
        var pages = container.GetBlobsByHierarchyAsync(BlobTraits.Metadata, delimiter: Delimiter.ToString(), prefix: path).AsPages(data.ContinuationToken, config.PageSizeHint);

        Page<BlobHierarchyItem>? workingPage = null;

        await foreach (var page in pages)
        {
            workingPage = page;
            break;
        }

        if (workingPage == null || workingPage.Values.Count == 0)
        {
            res.Message = new Message("Directory not found");
            return res;
        }

        res.ContinuationToken = workingPage.ContinuationToken;
        var canViewDeletedFiles = typeAuthService?.CanAccess(AzureStorageActionTree.ViewDeletedFiles) != false;

        try
        {
            if (!canViewDeletedFiles || !data.IncludeDeleted)
                await EnsurePathNotDeletedAsync(path);
        }
        catch (DirectoryNotFoundException)
        {
            res.Message = new Message("Directory not found");
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
                if (!canViewDeletedFiles || !data.IncludeDeleted)
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

                // Blob names could change after upload, display the original name in the UI
                // if not found, fallback to the current blob name
                entry.Name = HttpUtility.UrlDecode(originalName) ?? BlobHelper.GetName(item.Blob.Name);
                entry.Path = item.Blob.Name;
                entry.Type = Path.GetExtension(entry.Name)?.ToLower();
                entry.IsFile = true;
                entry.Size = item.Blob.Properties.ContentLength ?? 0;
                entry.CreatedDate = item.Blob.Properties.CreatedOn?.UtcDateTime ?? default;
                entry.DateModified = item.Blob.Properties.LastModified?.UtcDateTime ?? default;
                entry.CreatedBy = userId;

                entry.Url = azureStorageService.GetSignedURL(item.Blob.Name, BlobSasPermissions.Read, container.Name);

                if (entry.Type != null && ImageExtensions.Contains(entry.Type))
                {
                    item.Blob.Metadata.TryGetValue(Constants.FileExplorerSizesMetadataKey, out string? thumbnailSizes);
                    var prefix = $"{container.AccountName}_{container.Name}";
                    var size = thumbnailSizes?.Split("|").First() ?? "250x250";
                    var (dir, name) = BlobHelper.PathAndName(item.Blob.Name);
                    var blobName = $"{dir}{name}_{size}.png";
                    var thumbnailBlob = BlobHelper.Combine(prefix, blobName);
                    entry.ThumbnailUrl = azureStorageService.GetSignedURL(thumbnailBlob, BlobSasPermissions.Read, storageOption.ThumbnailContainerName, storageOption.AccountName);
                }
            }
            else if (item.IsPrefix)
            {
                entry.Name = BlobHelper.GetName(item.Prefix);
                entry.Type = "Directory";
                entry.Path = item.Prefix;
            }

            files.Add(entry);
        }

        if (fileExplorerAccessControl is not null)
        {
            var blobPaths = files.Select(x => x.Path!);
            var accessList = await this.fileExplorerAccessControl.FilterWithReadAccessAsync(container, blobPaths);
            files = files.Where(x => accessList.Contains(x.Path)).ToList();
        }

        res.Items = files;
        res.Success = true;

        return res;
    }

    public async Task<FileExplorerResponseDTO> Create(FileExplorerCreateDTO data)
    {
        // consider validating folder names to work with common OSs
        var res = new FileExplorerResponseDTO(data.Path);
        if (data.Path == null)
        {
            res.Message = new Message("Invalid path");
            return res;
        }

        var path = fileExplorerAccessControl == null
            ? data.Path
            : fileExplorerAccessControl.FilterWithWriteAccess([data.Path]).FirstOrDefault();

        var (dir, name) = BlobHelper.PathAndName(path);

        if (path == null || !BlobHelper.IsPathDirectory(path) || string.IsNullOrWhiteSpace(name))
        {
            res.Message = new Message("Invalid path");
            return res;
        }

        name = name.Trim();
        var newPath = dir + name + Delimiter;

        for (var i = 1; i <= MAX_CREATE_RETRY_ATTEMPTS; i++)
        {
            try
            {
                BlobClient blob = container.GetBlobClient(newPath + Constants.FileExplorerHiddenFilename);
                await blob.UploadAsync(BinaryData.FromBytes(Array.Empty<byte>()), overwrite: false);

                CreateLogItem(newPath, FileExplorerAction.Create);
                
                res.Success = true;
                res.Path = newPath;
                return res;
            }
            catch (RequestFailedException ex) when (ex.Status == 409)
            {
                // Status 409 == Blob already exists
                newPath = $"{dir}{name} ({i}){Delimiter}";
                continue;
            }
        }

        res.Message = new Message("Could not create folder");
        return res;
    }

    public async Task<FileExplorerResponseDTO> Delete(FileExplorerDeleteDTO data)
    {
        var res = new FileExplorerResponseDTO();
        var paths = fileExplorerAccessControl == null
            ? data.Paths.ToList()
            : fileExplorerAccessControl.FilterWithDeleteAccess(data.Paths);

        await QueryDeletedItems(paths.ToArray(), static (path, list) =>
        {
            if (!list.Contains(path))
                list.Add(path);
            return ValueTask.CompletedTask;
        });

        foreach (var path in paths)
            CreateLogItem(path, FileExplorerAction.Delete);
        res.Success = true;
        return res;
    }

    public async Task<FileExplorerResponseDTO> Restore(FileExplorerRestoreDTO data)
    {
        var res = new FileExplorerResponseDTO();

        var paths = fileExplorerAccessControl == null
            ? data.Paths.ToList()
            : fileExplorerAccessControl.FilterWithDeleteAccess(data.Paths);

        await QueryDeletedItems(paths.ToArray(), static (path, list) =>
        {
            list.RemoveAll(x => x == path);
            return ValueTask.CompletedTask;
        });

        foreach (var path in paths)
            CreateLogItem(path, FileExplorerAction.Restore);
        res.Success = true;
        return res;
    }

    private async Task QueryDeletedItems(string[] paths, Func<string, List<string>, ValueTask> callback)
    {
        var groupList = paths.Select(static path =>
        {
            var paths = path.Split(BlobHelper.Delimiter, StringSplitOptions.RemoveEmptyEntries).ToList();
            var name = paths.Last();
            paths.RemoveAt(paths.Count - 1);

            return new
            {
                name,
                IsFile = !path.EndsWith(BlobHelper.Delimiter),
                path = string.Join(BlobHelper.Delimiter, paths),
            };
        }).GroupBy(x => x.path);

        foreach (var group in groupList)
        {
            var path = group.Key;
            var (deletedList, blobClient) = await GetDeletedItems(path);

            foreach (var item in group)
            {
                var fullPath = BlobHelper.AppendDelimiter(BlobHelper.Combine(path, item.name), prepend: true);
                if (!item.IsFile)
                    fullPath = BlobHelper.AppendDelimiter(fullPath);

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

    public async Task<FileExplorerResponseDTO> Detail(FileExplorerDetailDTO data)
    {
        var path = data.Path ?? "";
        var isDir = path.EndsWith(Delimiter) || path == string.Empty;
        var res = new FileExplorerResponseDTO(path)
        {
            Items = [],
        };

        if (isDir)
        {
            var pages = container.GetBlobsAsync(prefix: path).AsPages(data.ContinuationToken, config.PageSizeHint);

            Page<BlobItem>? workingPage = null;

            await foreach (var page in pages)
            {
                workingPage = page;
                break;
            }

            if (workingPage == null || workingPage.Values.Count == 0)
            {
                res.Success = true;
                return res;
            }

            res.ContinuationToken = workingPage.ContinuationToken;
            var canViewDeletedFiles = typeAuthService?.CanAccess(AzureStorageActionTree.ViewDeletedFiles) != false;

            try
            {
                if (!canViewDeletedFiles || !data.IncludeDeleted)
                    await EnsurePathNotDeletedAsync(path);
            }
            catch (DirectoryNotFoundException)
            {
                res.Success = true;
                return res;
            }

            IEnumerable<string> deletedItems = [];

            var semaphore = new SemaphoreSlim(10);
            var deletedTask = workingPage.Values
                .Where(blob => blob.Name.EndsWith(Delimiter + Constants.FileExplorerHiddenFilename))
                .Select(async blob =>
                {
                    await semaphore.WaitAsync();
                    try { return await GetDeletedItems(blob.Name); }
                    finally { semaphore.Release(); }
                });
            var deletedResult = await Task.WhenAll(deletedTask);
            deletedItems = deletedResult.SelectMany(x => x.list);

            var blobs = canViewDeletedFiles && data.IncludeDeleted
                ? workingPage.Values
                : workingPage.Values.Where(blob => !deletedItems.Contains(Delimiter + blob.Name));

            int count = blobs.Count();
            var totalSize = blobs.Sum(blob => blob.Properties.ContentLength ?? 0);

            res.Items.Add(new FileExplorerItemDTO
            {
                Name = BlobHelper.GetName(path),
                Type = "Directory",
                Path = path,
                Size = totalSize,
                Additional = count,
            });
        }

        res.Success = true;
        return res;
    }

    public Task<FileExplorerResponseDTO> Copy(string[] names, string targetPath)
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

    private void CreateLogItem(string path, FileExplorerAction action)
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
        try
        {
            _ = cosmosContainer.CreateItemAsync(log, partKey);
        }
        catch (Exception) { }
    }

    public void PrepareBlobContainer(string? accountName, string? containerName)
    {
        this.container = azureStorageService.GetBlobContainerClient(accountName, containerName);
    }
}
