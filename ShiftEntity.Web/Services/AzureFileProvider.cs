using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Net;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Azure.Storage;
using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;
using System.Text;
using Azure.Storage.Blobs.Specialized;
using System.Text.Json;
using Azure;
using ShiftSoftware.ShiftEntity.Core;
using ShiftSoftware.ShiftEntity.Core.Services;
using Azure.Storage.Sas;
using ShiftSoftware.ShiftEntity.Model.Dtos;
using ShiftSoftware.ShiftEntity.Core.Extensions;

namespace ShiftSoftware.ShiftEntity.Web.Services
{
    public partial class AzureFileProvider
    {
        List<FileExplorerDirectoryContent> directoryContentItems = new List<FileExplorerDirectoryContent>();
        BlobContainerClient container;
        public string blobPath;
        public string filesPath;
        public string rootPath = string.Empty;
        long size;
        List<string> existFiles = new List<string>();
        List<string> missingFiles = new List<string>();
        bool isFolderAvailable = false;
        List<FileExplorerDirectoryContent> copiedFiles = new List<FileExplorerDirectoryContent>();
        DateTime lastUpdated = DateTime.MinValue;
        DateTime prevUpdated = DateTime.MinValue;

        private AzureStorageService? azureStorageService;
        private AzureStorageOption? AzureStorageOption;
        private readonly IFileExplorerAccessControl? fileExplorerAccessControl;

        private IEnumerable<string> ImageExtensions = new List<string>
        {
            ".jpg",
            ".jpeg",
            ".png",
            ".png",
            ".webp",
        };

        public AzureFileProvider(AzureStorageService azureStorageService, IFileExplorerAccessControl? fileExplorerAccessControl)
        {
            this.azureStorageService = azureStorageService;
            this.SetContainer();
            this.fileExplorerAccessControl = fileExplorerAccessControl;
        }

        public void SetRootDirectory(string root)
        {
            filesPath = blobPath.AddUrlPath(root).Replace("../", "");
            rootPath = filesPath.Replace(blobPath, "");
        }

        public void SetContainer(string? accountName = null, string? containerName = null)
        {
            if (azureStorageService == null) return;
        
            var _accountName = accountName ?? azureStorageService.GetDefaultAccountName();
            var _containerName = containerName ?? azureStorageService.GetDefaultContainerName(_accountName);
            azureStorageService.azureStorageAccounts.TryGetValue(_accountName, out AzureStorageOption);

            if (AzureStorageOption?.SupportsFileExplorer == false)
            {
                throw new Exception($"FileExplorer not supported for storage account ({_accountName})");
            }

            BlobServiceClient? client = null;
            azureStorageService?.blobServiceClients.TryGetValue(_accountName, out client);

            if (client == null)
            {
                throw new Exception($"Blob container not found ({_containerName})");
            }

            container = client.GetBlobContainerClient(_containerName);

            blobPath = container.Uri.ToString();
            filesPath = blobPath;
        }

        // Reads the storage 
        public FileExplorerResponse GetFiles(string path, bool showHiddenItems, FileExplorerDirectoryContent[] selectedItems)
        {
            return GetFilesAsync(path, "*.*", showHiddenItems, selectedItems).GetAwaiter().GetResult();
        }

        // Reads the storage files
        protected async Task<FileExplorerResponse> GetFilesAsync(string path, string filter, bool ViewDeleted, FileExplorerDirectoryContent[] selectedItems)
        {
            // make sure the path doesn't start with and is not '/'
            path = path.TrimStart('/');

            FileExplorerResponse readResponse = new FileExplorerResponse();
            List<string> prefixes = new List<string>();
            List<FileExplorerDirectoryContent> details = new List<FileExplorerDirectoryContent>();
            FileExplorerDirectoryContent cwd = new FileExplorerDirectoryContent();
            try
            {
                // Check if there are any items in this dir or any sub dirs
                // We only need to find one item
                var blobPages = container.GetBlobsAsync(prefix: path).AsPages(pageSizeHint: 1).GetAsyncEnumerator();
                await blobPages.MoveNextAsync();

                if (!blobPages.Current.Values.Any())
                {
                    readResponse.CWD = cwd;
                    ErrorDetails errorDetails = new ErrorDetails();
                    errorDetails.Message = "Could not find a part of the path '" + path + "'.";
                    errorDetails.Code = "417";
                    readResponse.Error = errorDetails;
                    return readResponse;
                }

                // Check if current path is in a deleted directory
                var paths = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
                var currentPath = "";
                var dl = new List<string>();

                if (!ViewDeleted && paths.Count() > 0)
                {
                    // get all deleted files in parent directories
                    foreach (var pathPart in paths)
                    {
                        var blobPath = currentPath + Constants.FileExplorerHiddenFilename;
                        var d = container.GetBlobClient(blobPath);

                        if (await d.ExistsAsync())
                        {
                            BlobDownloadInfo download = await d.DownloadAsync();
                            using var reader = new StreamReader(download.Content, Encoding.UTF8);
                            dl.AddRange((await reader.ReadToEndAsync()).Split('\n', StringSplitOptions.RemoveEmptyEntries));
                        }

                        currentPath += pathPart + "/";
                    }

                    if (dl.Count > 0 && dl.Any(x => $"/{path}".StartsWith(x)))
                    {
                        cwd.Path = path;
                        cwd.Name = path;
                        readResponse.CWD = cwd;
                        ErrorDetails errorDetails = new ErrorDetails();
                        errorDetails.Message = "Could not find a part of the path '" + path + "'.";
                        errorDetails.Code = "417";
                        readResponse.Error = errorDetails;
                        return readResponse;
                    }
                }

                string[] extensions = (filter.Replace(" ", "") ?? "*").Split(",|;".ToCharArray(), StringSplitOptions.RemoveEmptyEntries);
                var noFilter = extensions[0].Equals("*.*") || extensions[0].Equals("*");
                cwd.Name = selectedItems.Length != 0 ? selectedItems[0].Name : string.IsNullOrWhiteSpace(path) ? "/" : path.Split('/', StringSplitOptions.RemoveEmptyEntries).Last();
                //container.GetBlobClient("/" + path);
                cwd.Type = "File Folder";
                cwd.FilterPath = selectedItems.Length != 0 ? selectedItems[0].FilterPath : "";
                cwd.Size = 0;
                cwd.Path = path;

                // get the list of deleted items in the current dir
                var deletedFilesBlob = container.GetBlobClient(path + Constants.FileExplorerHiddenFilename);
                var deletedList = new List<string>();

                if (await deletedFilesBlob.ExistsAsync())
                {
                    BlobDownloadInfo download = await deletedFilesBlob.DownloadAsync();
                    using var reader = new StreamReader(download.Content, Encoding.UTF8);
                    deletedList = (await reader.ReadToEndAsync()).Split('\n', StringSplitOptions.RemoveEmptyEntries).ToList();
                }


                var filterPath = "/";

                if (selectedItems.Length > 0)
                {
                    filterPath += path;

                    // Remove rootPath from filterPath
                    if (!string.IsNullOrWhiteSpace(rootPath))
                    {
                        var index = filterPath.IndexOf(rootPath, StringComparison.Ordinal);

                        if (index >= 0)
                        {
                            filterPath = filterPath.Remove(index, rootPath.Length);
                        }
                    }
                }

                foreach (Page<BlobHierarchyItem> page in container.GetBlobsByHierarchy(delimiter: "/", prefix: path).AsPages())
                {
                    foreach (BlobItem item in page.Values.Where(x => x.IsBlob).Select(x => x.Blob))
                    {
                        var isHidden = item.Metadata.TryGetValue(Constants.FileExplorerHiddenMetadataKey, out _);
                        var isHiddenEmptyFile = item.Name.EndsWith(Constants.FileExplorerHiddenFilename);
                        var fileTypeIsFiltered = Array.IndexOf(extensions, "*." + item.Name.ToString().Trim().Split('.')[item.Name.ToString().Trim().Split('.').Length - 1]) < 0;
                        var skip = (!noFilter && fileTypeIsFiltered) || isHiddenEmptyFile || isHidden;
                        if (skip) continue;

                        var entry = new FileExplorerDirectoryContent();

                        if (deletedList.Contains("/" + item.Name))
                        {
                            if (ViewDeleted)
                            {
                                entry.IsDeleted = true;
                            }
                            else
                            {
                                continue;
                            }
                        }

                        item.Metadata.TryGetValue("name", out string? originalName);

                        entry.Name = originalName ?? GetName(item.Name, path);
                        entry.Path = item.Name;
                        entry.Type = Path.GetExtension(entry.Name);
                        entry.IsFile = true;
                        entry.Size = item.Properties.ContentLength ?? 0;
                        entry.DateModified = item.Properties.LastModified?.LocalDateTime ?? default;
                        entry.HasChild = false;
                        entry.FilterPath = filterPath;

                        // Get the signed URL for the thumbnail file
                        if (AzureStorageOption != null && ImageExtensions.Contains(Path.GetExtension(entry.Name).ToLower()))
                        {
                            var prefix = container.AccountName + "_" + container.Name;
                            var size = "250x250";
                            // GetFileNameWithoutExtension also removes the dir
                            var dir =  Path.GetDirectoryName(item.Name)?.TrimEnd('/');
                            var name = Path.GetFileNameWithoutExtension(item.Name);
                            var blobName = $"{dir}/{name}_{size}.png";
                            var thumbnailBlob = prefix.AddUrlPath(blobName);
                            entry.ThumbnailUrl = azureStorageService?.GetSignedURL(thumbnailBlob, BlobSasPermissions.Read, AzureStorageOption.ThumbnailContainerName, AzureStorageOption.AccountName);
                        }

                        // Get the signed URL for the file
                        entry.TargetPath = azureStorageService?.GetSignedURL(item.Name, BlobSasPermissions.Read, container.Name);

                        details.Add(entry);
                    }

                    foreach (string item in page.Values.Where(x => x.IsPrefix).Select(x => x.Prefix))
                    {
                        var entry = new FileExplorerDirectoryContent();
                        string dir = item;

                        if (deletedList.Contains("/" + dir))
                        {
                            if (ViewDeleted)
                            {
                                entry.IsDeleted = true;
                            }
                            else
                            {
                                continue;
                            }
                        }

                        entry.Name = GetName(dir, path);
                        entry.Type = "Directory";
                        entry.IsFile = false;
                        entry.Size = 0;
                        entry.Path = item;
                        entry.HasChild = false;
                        entry.FilterPath = filterPath;
                        //entry.DateModified = await DirectoryLastModified(dir);
                        lastUpdated = prevUpdated = DateTime.MinValue;
                        details.Add(entry);
                    }

                    prefixes = page.Values.Where(x => x.IsPrefix).Select(x => x.Prefix).ToList();
                }

                if (this.fileExplorerAccessControl is not null)
                {
                    details = await this.fileExplorerAccessControl.FilterWithReadAccessAsync(container, details);
                }

                cwd.HasChild = prefixes?.Count != 0;
                readResponse.CWD = cwd;
            }
            catch (Exception e)
            {
                readResponse.CWD = cwd;
                ErrorDetails errorDetails = new ErrorDetails();
                errorDetails.Message = e.Message.ToString();
                errorDetails.Code = "417";
                readResponse.Error = errorDetails;
                return readResponse;
            }

            readResponse.Files = details;
            return readResponse;
        }

        private string GetName(string name, string path)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                return name.TrimEnd('/');
            }
            else
            {
                var nameRegex = ExtractNameRegex();
                var result = nameRegex.Match(name);
                var fileName = result.Groups.Values.LastOrDefault()?.Value;
                fileName ??= name.Replace(path, "").Replace("/", "");
                return fileName;
            }
        }

        // Returns the last modified date for directories
        protected async Task<DateTime> DirectoryLastModified(string path)
        {
            foreach (Page<BlobItem> page in container.GetBlobs(prefix: path).AsPages())
            {
                DateTime checkFileModified = page.Values.ToList().OrderByDescending(m => m.Properties.LastModified).ToList().First().Properties.LastModified.Value.LocalDateTime;
                lastUpdated = prevUpdated = prevUpdated < checkFileModified ? checkFileModified : prevUpdated;
            }
            return lastUpdated;
        }

        // Converts the byte size value to appropriate value
        protected string ByteConversion(long fileSize)
        {
            try
            {
                string[] index = { "B", "KB", "MB", "GB", "TB", "PB", "EB" };
                // Longs run out around EB
                if (fileSize == 0)
                {
                    return "0 " + index[0];
                }
                int value = Convert.ToInt32(Math.Floor(Math.Log(Math.Abs(fileSize), 1024)));
                return (Math.Sign(fileSize) * Math.Round(Math.Abs(fileSize) / Math.Pow(1024, value), 1)).ToString() + " " + index[value];
            }
            catch (Exception)
            {
                throw;
            }
        }

        // Gets the size value of the directory
        protected async Task<long> GetSizeValue(string path)
        {
            foreach (Page<BlobItem> page in container.GetBlobs(prefix: path + "/").AsPages())
            {
                foreach (BlobItem item in page.Values)
                {
                    BlobClient blob = container.GetBlobClient(item.Name);
                    BlobProperties properties = await blob.GetPropertiesAsync();
                    size += properties.ContentLength;
                }
            }
            return size;
        }

        // Gets details of the files
        public FileExplorerResponse Details(string path, string[] names, params FileExplorerDirectoryContent[] data)
        {
            return GetDetailsAsync(path, names, data).GetAwaiter().GetResult();
        }

        // Gets the details
        protected async Task<FileExplorerResponse> GetDetailsAsync(string path, string[] names, IEnumerable<object> selectedItems = null)
        {
            bool isVariousFolders = false;
            string previousPath = "";
            string previousName = "";
            FileExplorerResponse detailsResponse = new FileExplorerResponse();
            try
            {
                bool isFile = false;
                bool namesAvailable = names.Length > 0;
                if (names.Length == 0 && selectedItems != null)
                {
                    List<string> values = new List<string>();
                    foreach (FileExplorerDirectoryContent item in selectedItems)
                    {
                        values.Add(item.Name);
                    }
                    names = values.ToArray();
                }
                FileDetails fileDetails = new FileDetails();
                long multipleSize = 0;
                if (selectedItems != null)
                {
                    foreach (FileExplorerDirectoryContent fileItem in selectedItems)
                    {
                        if (names.Length == 1)
                        {
                            if (fileItem.IsFile)
                            {
                                BlobClient blob = container.GetBlobClient(rootPath + fileItem.FilterPath + fileItem.Name);
                                BlobProperties properties = await blob.GetPropertiesAsync();
                                isFile = fileItem.IsFile;
                                fileDetails.IsFile = isFile;
                                fileDetails.Name = fileItem.Name;
                                fileDetails.Location = (namesAvailable ? rootPath + fileItem.FilterPath + fileItem.Name : path.TrimEnd('/')).Replace("/", @"\");
                                fileDetails.Size = ByteConversion(properties.ContentLength); fileDetails.Modified = properties.LastModified.LocalDateTime; detailsResponse.Details = fileDetails;
                            }
                            else
                            {
                                long sizeValue = GetSizeValue(namesAvailable ? rootPath + fileItem.FilterPath + fileItem.Name : path.TrimEnd('/')).Result;
                                isFile = false;
                                fileDetails.Name = fileItem.Name;
                                fileDetails.Location = (namesAvailable ? rootPath + fileItem.FilterPath + fileItem.Name : path.Substring(0, path.Length - 1)).Replace("/", @"\");
                                fileDetails.Size = ByteConversion(sizeValue); fileDetails.Modified = await DirectoryLastModified(path.TrimStart('/')); detailsResponse.Details = fileDetails;
                            }
                        }
                        else
                        {
                            multipleSize += fileItem.IsFile ? fileItem.Size : GetSizeValue(namesAvailable ? rootPath + fileItem.FilterPath + fileItem.Name : path).Result;
                            size = 0;
                            fileDetails.Name = previousName == "" ? previousName = fileItem.Name : previousName + ", " + fileItem.Name;
                            previousPath = previousPath == "" ? rootPath + fileItem.FilterPath : previousPath;
                            if (previousPath == rootPath + fileItem.FilterPath && !isVariousFolders)
                            {
                                previousPath = rootPath + fileItem.FilterPath;
                                fileDetails.Location = (rootPath + fileItem.FilterPath).Replace("/", @"\").Substring(0, (rootPath + fileItem.FilterPath).Replace(" / ", @"\").Length - 1);
                            }
                            else
                            {
                                isVariousFolders = true;
                                fileDetails.Location = "Various Folders";
                            }
                            fileDetails.Size = ByteConversion(multipleSize); fileDetails.MultipleFiles = true; detailsResponse.Details = fileDetails;
                        }
                    }
                }
                return await Task.Run(() =>
                {
                    size = 0;
                    return detailsResponse;
                });
            }
            catch (Exception)
            {
                throw;
            }
        }

        // Creates a new folder
        public FileExplorerResponse Create(string path, string name, params FileExplorerDirectoryContent[] selectedItems)
        {
            isFolderAvailable = false;
            FileExplorerResponse createResponse = new FileExplorerResponse();
            try
            {
                CreateFolderAsync(path, name, selectedItems).GetAwaiter().GetResult();
                if (isFolderAvailable)
                {
                    return Create(path, $"{name} ({Guid.NewGuid().ToString().Substring(0, 4)})", selectedItems);
                }
                else
                {
                    FileExplorerDirectoryContent content = new FileExplorerDirectoryContent();
                    content.Name = name;
                    FileExplorerDirectoryContent[] directories = new[] { content };
                    createResponse.Files = (IEnumerable<FileExplorerDirectoryContent>)directories;
                }

                return createResponse;
            }
            catch (Exception e)
            {
                ErrorDetails er = new ErrorDetails();
                er.Message = e.Message.ToString();
                er.Code = "417";
                createResponse.Error = er;
                return createResponse;
            }
        }

        // Creates a new folder
        protected async Task CreateFolderAsync(string path, string name, IEnumerable<object> selectedItems = null)
        {
            string checkName = name.Contains(" ") ? name.Replace(" ", "%20") : name;
            foreach (Page<BlobHierarchyItem> page in container.GetBlobsByHierarchy(prefix: path, delimiter: "/").AsPages())
            {
                List<BlobItem> items = page.Values.Where(item => item.IsBlob).Select(item => item.Blob).ToList();
                if (await IsFolderExists(path + name) || items.Where(x => x.Name.Split("/").Last().Replace("/", "").ToLower() == checkName.ToLower()).Select(i => i).ToArray().Length > 0)
                {
                    isFolderAvailable = true;
                }
                else
                {
                    BlobClient blob = container.GetBlobClient(path + name + "/" + Constants.FileExplorerHiddenFilename);
                    await blob.UploadAsync(new MemoryStream());
                }
            }
        }

        // Renames file(s) or folder(s)
        public FileExplorerResponse Rename(string path, string oldName, string newName, bool replace = false, bool showFileExtension = true, params FileExplorerDirectoryContent[] data)
        {
            throw new NotImplementedException();
            //return RenameAsync(path, oldName, newName, showFileExtension, data).GetAwaiter().GetResult();
        }

        // Renames file(s) or folder(s)
        protected async Task<FileExplorerResponse> RenameAsync(string path, string oldName, string newName, bool showFileExtension, params FileExplorerDirectoryContent[] selectedItems)
        {
            FileExplorerResponse renameResponse = new FileExplorerResponse();
            List<FileExplorerDirectoryContent> details = new List<FileExplorerDirectoryContent>();
            FileExplorerDirectoryContent entry = new FileExplorerDirectoryContent();
            try
            {
                bool isAlreadyAvailable = false;
                bool isFile = false;
                foreach (FileExplorerDirectoryContent fileItem in selectedItems)
                {
                    FileExplorerDirectoryContent directoryContent = fileItem;
                    isFile = directoryContent.IsFile;
                    isAlreadyAvailable = await IsFileExists(path + newName);
                    entry.Name = newName;
                    entry.Type = directoryContent.Type;
                    entry.IsFile = isFile;
                    entry.Size = directoryContent.Size;
                    entry.HasChild = directoryContent.HasChild;
                    entry.FilterPath = directoryContent.FilterPath;
                    details.Add(entry);
                    break;
                }
                if (!isAlreadyAvailable)
                {
                    if (isFile)
                    {
                        if (!showFileExtension)
                        {
                            oldName = oldName + selectedItems[0].Type;
                            newName = newName + selectedItems[0].Type;
                        }
                        BlobClient existBlob = container.GetBlobClient(path + oldName);
                        await container.GetBlobClient(path + newName).StartCopyFromUriAsync(existBlob.Uri);
                        await existBlob.DeleteAsync();
                    }
                    else
                    {
                        foreach (Page<BlobItem> page in container.GetBlobs(prefix: path + oldName + "/").AsPages())
                        {
                            foreach (BlobItem item in page.Values)
                            {
                                string name = Uri.UnescapeDataString(container.GetBlobClient(item.Name).Uri.AbsolutePath.Replace(container.GetBlobClient(path + oldName).Uri.AbsolutePath + "/", "").Replace("%20", " "));
                                await container.GetBlobClient(path + newName + "/" + name).StartCopyFromUriAsync(container.GetBlobClient(item.Name).Uri);
                                await container.GetBlobClient(path + oldName + "/" + name).DeleteAsync();
                            }
                        }
                    }
                    renameResponse.Files = details;
                }
                else
                {
                    ErrorDetails error = new ErrorDetails();
                    error.FileExists = existFiles;
                    error.Code = "400";
                    error.Message = "File or Folder Already Exists";
                    renameResponse.Error = error;
                }
                return renameResponse;
            }
            catch (Exception e)
            {
                ErrorDetails er = new ErrorDetails();
                er.Message = e.Message.ToString();
                er.Code = "417";
                renameResponse.Error = er;
                return renameResponse;
            }
        }

        // Deletes file(s) or folder(s)
        public FileExplorerResponse Delete(string path, string[] names, bool softDelete, params FileExplorerDirectoryContent[] data)
        {
            var newData = new List<FileExplorerDirectoryContent>();

            if (this.fileExplorerAccessControl is not null)
            {
                newData = this.fileExplorerAccessControl.FilterWithDeleteAccess(data);
            }
            else
            {
                newData = data.ToList();
            }

            return RemoveAsync(names, path, softDelete, newData).GetAwaiter().GetResult();
        }

        // Deletes file(s) or folder(s)
        protected async Task<FileExplorerResponse> RemoveAsync(string[] names, string path, bool softDelete, List<FileExplorerDirectoryContent> selectedItems)
        {
            FileExplorerResponse removeResponse = new FileExplorerResponse();
            List<FileExplorerDirectoryContent> details = new List<FileExplorerDirectoryContent>();
            FileExplorerDirectoryContent entry = new FileExplorerDirectoryContent();
            try
            {
                BlobClient? blobClient = null;
                var deletedList = string.Empty;
                var addToDeleted = string.Empty;
                
                if (softDelete)
                {
                    blobClient = container.GetBlobClient(path + Constants.FileExplorerHiddenFilename);
                }

                if (blobClient != null && await blobClient.ExistsAsync())
                {
                    BlobDownloadInfo download = await blobClient.DownloadAsync();
                    using (var reader = new StreamReader(download.Content, Encoding.UTF8))
                    {
                        deletedList = await reader.ReadToEndAsync();
                    }
                }

                foreach (FileExplorerDirectoryContent fileItem in selectedItems)
                {
                    path = filesPath.Replace(blobPath, "") + fileItem.FilterPath;

                    if (softDelete)
                    {
                        var textToAppend = "/" + fileItem.Path;
                        addToDeleted += textToAppend + "\n";
                    }

                    if (fileItem.IsFile)
                    {
                        if (!softDelete)
                        {
                            BlobClient currentFile = container.GetBlobClient(path + fileItem.Name);
                            currentFile.DeleteIfExists();
                        }

                        entry.Name = fileItem.Name;
                        entry.Type = fileItem.Type;
                        entry.IsFile = fileItem.IsFile;
                        entry.Size = fileItem.Size;
                        entry.HasChild = fileItem.HasChild;
                        entry.FilterPath = path;
                        details.Add(entry);
                    }
                    else
                    {

                        foreach (Page<BlobItem> items in container.GetBlobs(prefix: path + fileItem.Name + "/").AsPages())
                        {
                            if (!softDelete)
                            {
                                foreach (BlobItem item in items.Values)
                                {
                                    BlobClient currentFile = container.GetBlobClient(item.Name);
                                    await currentFile.DeleteAsync();
                                }
                            }

                            entry.Name = fileItem.Name;
                            entry.Type = fileItem.Type;
                            entry.IsFile = fileItem.IsFile;
                            entry.Size = fileItem.Size;
                            entry.HasChild = fileItem.HasChild;
                            entry.FilterPath = path;
                            details.Add(entry);
                        }
                    }
                }

                using (var stream = await blobClient.OpenWriteAsync(overwrite: true))
                {
                    var newContent = deletedList + addToDeleted;
                    byte[] newContentBytes = Encoding.UTF8.GetBytes(newContent);
                    await stream.WriteAsync(newContentBytes, 0, newContentBytes.Length);
                }
            }
            catch (Exception e)
            {
                ErrorDetails er = new ErrorDetails();
                er.Message = e.Message.ToString();
                er.Code = "417";
                removeResponse.Error = er;
                return removeResponse;
            }
            removeResponse.Files = details;
            return removeResponse;
        }

        // Check whether the directory has child
        private async Task<bool> HasChildDirectory(string path)
        {
            List<string> prefixes = new List<string>() { };
            foreach (Page<BlobHierarchyItem> page in container.GetBlobsByHierarchy(prefix: path, delimiter: "/").AsPages())
            {
                prefixes = page.Values.Where(item => item.IsPrefix).Select(item => item.Prefix).ToList();
            }
            return prefixes.Count != 0;
        }

        // To get the file details
        private static FileExplorerDirectoryContent GetFileDetails(string targetPath, FileExplorerDirectoryContent fileDetails)
        {
            FileExplorerDirectoryContent entry = new FileExplorerDirectoryContent();
            entry.Name = fileDetails.Name;
            entry.Type = fileDetails.Type;
            entry.IsFile = fileDetails.IsFile;
            entry.Size = fileDetails.Size;
            entry.HasChild = fileDetails.HasChild;
            //entry.FilterPath = targetPath.Replace(rootPath, "");
            return entry;
        }

        // To check if folder exists
        private async Task<bool> IsFolderExists(string path)
        {
            List<string> x = new List<string>() { };
            foreach (Page<BlobHierarchyItem> page in container.GetBlobsByHierarchy(prefix: path, delimiter: "/").AsPages())
            {
                x = page.Values.Where(item => item.IsPrefix).Select(item => item.Prefix).ToList();
            }
            return x.Count > 0;
        }

        // To check if file exists
        private async Task<bool> IsFileExists(string path)
        {
            BlobClient newBlob = container.GetBlobClient(path);
            return await newBlob.ExistsAsync();
        }

        // Copies file(s) or folders
        public FileExplorerResponse Copy(string path, string targetPath, string[] names, string[] renameFiles, FileExplorerDirectoryContent targetData, params FileExplorerDirectoryContent[] data)
        {
            throw new NotImplementedException();
            //return CopyToAsync(path, targetPath, names, renameFiles, data).GetAwaiter().GetResult();
        }

        //private async Task<FileManagerResponse> CopyToAsync(string path, string targetPath, string[] names, string[] renamedFiles = null, params FileExplorerDirectoryContent[] data)
        //{
        //    FileManagerResponse copyResponse = new FileManagerResponse();
        //    HashSet<string> processedItems = new HashSet<string>();
        //    try
        //    {
        //        renamedFiles = renamedFiles ?? Array.Empty<string>();
        //        foreach (FileExplorerDirectoryContent item in data)
        //        {
        //            if (processedItems.Contains(item.Name))
        //            {
        //                continue;
        //            }
        //            processedItems.Add(item.Name);

        //            if (item.IsFile)
        //            {
        //                if (await IsFileExists(targetPath + item.Name))
        //                {
        //                    int index = -1;
        //                    if (renamedFiles.Length > 0)
        //                    {
        //                        index = Array.FindIndex(renamedFiles, Items => Items.Contains(item.Name));
        //                    }
        //                    if (index != -1)
        //                    {
        //                        string newName = await FileRename(targetPath, item.Name);
        //                        CopyItems(rootPath + item.FilterPath, targetPath, item.Name, newName);
        //                        copiedFiles.Add(GetFileDetails(targetPath, item));
        //                    }
        //                    else
        //                    {
        //                        existFiles.Add(item.Name);
        //                    }
        //                }
        //                else
        //                {
        //                    CopyItems(rootPath + item.FilterPath, targetPath, item.Name, null);
        //                    copiedFiles.Add(GetFileDetails(targetPath, item));
        //                }
        //            }
        //            else
        //            {
        //                if (!await IsFolderExists(rootPath + item.FilterPath + item.Name))
        //                {
        //                    missingFiles.Add(item.Name);
        //                }
        //                else if (await IsFolderExists(targetPath + item.Name))
        //                {
        //                    int index = -1;
        //                    if (renamedFiles.Length > 0)
        //                    {
        //                        index = Array.FindIndex(renamedFiles, Items => Items.Contains(item.Name));
        //                    }
        //                    if (path == targetPath || index != -1)
        //                    {
        //                        item.Path = rootPath + item.FilterPath + item.Name;
        //                        item.Name = await FileRename(targetPath, item.Name);
        //                        CopySubFolder(item, targetPath);
        //                        copiedFiles.Add(GetFileDetails(targetPath, item));
        //                    }
        //                    else
        //                    {
        //                        existFiles.Add(item.Name);
        //                    }
        //                }
        //                else
        //                {
        //                    item.Path = rootPath + item.FilterPath + item.Name;
        //                    CopySubFolder(item, targetPath);
        //                    copiedFiles.Add(GetFileDetails(targetPath, item));
        //                }
        //            }

        //        }
        //        copyResponse.Files = copiedFiles;
        //        if (existFiles.Count > 0)
        //        {
        //            ErrorDetails error = new ErrorDetails();
        //            error.FileExists = existFiles;
        //            error.Code = "400";
        //            error.Message = "File Already Exists";
        //            copyResponse.Error = error;
        //        }
        //        if (missingFiles.Count > 0)
        //        {
        //            string missingFilesList = missingFiles[0];
        //            for (int k = 1; k < missingFiles.Count; k++)
        //            {
        //                missingFilesList = missingFilesList + ", " + missingFiles[k];
        //            }
        //            throw new FileNotFoundException(missingFilesList + " not found in given location.");
        //        }
        //        return copyResponse;
        //    }
        //    catch (Exception e)
        //    {
        //        ErrorDetails error = new ErrorDetails();
        //        error.Message = e.Message.ToString();
        //        error.Code = "404";
        //        error.FileExists = copyResponse.Error?.FileExists;
        //        copyResponse.Error = error;
        //        return copyResponse;
        //    }
        //}

        // To iterate and copy subfolder
        //private void CopySubFolder(FileExplorerDirectoryContent subFolder, string targetPath)
        //{
        //    targetPath = targetPath + subFolder.Name + "/";
        //    foreach (Page<BlobHierarchyItem> page in container.GetBlobsByHierarchy(prefix: subFolder.Path + "/", delimiter: "/").AsPages())
        //    {
        //        foreach (BlobItem item in page.Values.Where(item => item.IsBlob).Select(item => item.Blob))
        //        {
        //            string name = item.Name.Replace(subFolder.Path + "/", "");
        //            string sourcePath = item.Name.Replace(name, "");
        //            CopyItems(sourcePath, targetPath, name, null);
        //        }
        //        foreach (string item in page.Values.Where(item => item.IsPrefix).Select(item => item.Prefix))
        //        {
        //            FileExplorerDirectoryContent itemDetail = new FileExplorerDirectoryContent();
        //            itemDetail.Name = item.Replace(subFolder.Path, "").Replace("/", "");
        //            itemDetail.Path = subFolder.Path + "/" + itemDetail.Name;
        //            CopySubFolder(itemDetail, targetPath);
        //        }
        //    }
        //}

        // To iterate and copy files
        //private void CopyItems(string sourcePath, string targetPath, string name, string newName)
        //{
        //    if (newName == null)
        //    {
        //        newName = name;
        //    }
        //    BlobClient existBlob = container.GetBlobClient(sourcePath + name);
        //    BlobClient newBlob = container.GetBlobClient(targetPath + newName);
        //    newBlob.StartCopyFromUri(existBlob.Uri);
        //}

        // To rename files incase of duplicates
        private async Task<string> FileRename(string newPath, string fileName)
        {
            int index = fileName.LastIndexOf(".");
            string nameNotExist = string.Empty;
            nameNotExist = index >= 0 ? fileName.Substring(0, index) : fileName;
            int fileCount = 0;
            while (index > -1 ? await IsFileExists(newPath + nameNotExist + (fileCount > 0 ? "(" + fileCount.ToString() + ")" + Path.GetExtension(fileName) : Path.GetExtension(fileName))) : await IsFolderExists(newPath + nameNotExist + (fileCount > 0 ? "(" + fileCount.ToString() + ")" + Path.GetExtension(fileName) : Path.GetExtension(fileName))))
            {
                fileCount++;
            }
            fileName = nameNotExist + (fileCount > 0 ? "(" + fileCount.ToString() + ")" : "") + Path.GetExtension(fileName);
            return await Task.Run(() =>
            {
                return fileName;
            });
        }

        //private async Task MoveItems(string sourcePath, string targetPath, string name, string newName)
        //{
        //    BlobClient existBlob = container.GetBlobClient(sourcePath + name);
        //    CopyItems(sourcePath, targetPath, name, newName);
        //    await existBlob.DeleteAsync();
        //}

        //private async void MoveSubFolder(FileExplorerDirectoryContent subFolder, string targetPath)
        //{
        //    targetPath = targetPath + subFolder.Name + "/";
        //    foreach (Page<BlobHierarchyItem> page in container.GetBlobsByHierarchy(prefix: subFolder.Path + "/", delimiter: "/").AsPages())
        //    {
        //        foreach (BlobItem item in page.Values.Where(item => item.IsBlob).Select(item => item.Blob))
        //        {
        //            string name = item.Name.Replace(subFolder.Path + "/", "");
        //            string sourcePath = item.Name.Replace(name, "");
        //            await MoveItems(sourcePath, targetPath, name, null);
        //        }
        //        foreach (string item in page.Values.Where(item => item.IsPrefix).Select(item => item.Prefix))
        //        {
        //            FileExplorerDirectoryContent itemDetail = new FileExplorerDirectoryContent();
        //            itemDetail.Name = item.Replace(subFolder.Path, "").Replace("/", "");
        //            itemDetail.Path = subFolder.Path + "/" + itemDetail.Name;
        //            MoveSubFolder(itemDetail, targetPath);
        //        }
        //    }
        //}

        // Moves file(s) or folders
        public FileExplorerResponse Move(string path, string targetPath, string[] names, string[] renameFiles, FileExplorerDirectoryContent targetData, params FileExplorerDirectoryContent[] data)
        {
            throw new NotImplementedException();
            //return MoveToAsync(path, targetPath, names, renameFiles, data).GetAwaiter().GetResult();
        }

        //private async Task<FileManagerResponse> MoveToAsync(string path, string targetPath, string[] names, string[] renamedFiles = null, params FileExplorerDirectoryContent[] data)
        //{
        //    FileManagerResponse moveResponse = new FileManagerResponse();
        //    try
        //    {
        //        renamedFiles = renamedFiles ?? Array.Empty<string>();
        //        foreach (FileExplorerDirectoryContent item in data)
        //        {
        //            if (item.IsFile)
        //            {
        //                if (await IsFileExists(targetPath + item.Name))
        //                {
        //                    int index = -1;
        //                    if (renamedFiles.Length > 0)
        //                    {
        //                        index = Array.FindIndex(renamedFiles, Items => Items.Contains(item.Name));
        //                    }
        //                    if (path == targetPath || index != -1)
        //                    {
        //                        string newName = await FileRename(targetPath, item.Name);
        //                        await MoveItems(rootPath + item.FilterPath, targetPath, item.Name, newName);
        //                        copiedFiles.Add(GetFileDetails(targetPath, item));
        //                    }
        //                    else
        //                    {
        //                        existFiles.Add(item.Name);
        //                    }
        //                }
        //                else
        //                {
        //                    await MoveItems(rootPath + item.FilterPath, targetPath, item.Name, null);
        //                    copiedFiles.Add(GetFileDetails(targetPath, item));
        //                }
        //            }
        //            else
        //            {
        //                if (!await IsFolderExists(rootPath + item.FilterPath + item.Name))
        //                {
        //                    missingFiles.Add(item.Name);
        //                }
        //                else if (await IsFolderExists(targetPath + item.Name))
        //                {
        //                    int index = -1;
        //                    if (renamedFiles.Length > 0)
        //                    {
        //                        index = Array.FindIndex(renamedFiles, Items => Items.Contains(item.Name));
        //                    }
        //                    if (path == targetPath || index != -1)
        //                    {
        //                        item.Path = rootPath + item.FilterPath + item.Name;
        //                        item.Name = await FileRename(targetPath, item.Name);
        //                        MoveSubFolder(item, targetPath);
        //                        copiedFiles.Add(GetFileDetails(targetPath, item));
        //                    }
        //                    else
        //                    {
        //                        existFiles.Add(item.Name);
        //                    }
        //                }
        //                else
        //                {
        //                    item.Path = rootPath + item.FilterPath + item.Name;
        //                    MoveSubFolder(item, targetPath);
        //                    copiedFiles.Add(GetFileDetails(targetPath, item));
        //                }
        //            }
        //        }
        //        moveResponse.Files = copiedFiles;
        //        if (existFiles.Count > 0)
        //        {
        //            ErrorDetails error = new ErrorDetails();
        //            error.FileExists = existFiles;
        //            error.Code = "400";
        //            error.Message = "File Already Exists";
        //            moveResponse.Error = error;
        //        }
        //        if (missingFiles.Count > 0)
        //        {
        //            string nameList = missingFiles[0];
        //            for (int k = 1; k < missingFiles.Count; k++)
        //            {
        //                nameList = nameList + ", " + missingFiles[k];
        //            }
        //            throw new FileNotFoundException(nameList + " not found in given location.");
        //        }
        //        return moveResponse;
        //    }
        //    catch (Exception e)
        //    {
        //        ErrorDetails error = new ErrorDetails();
        //        error.Message = e.Message.ToString();
        //        error.Code = "404";
        //        error.FileExists = moveResponse.Error?.FileExists;
        //        moveResponse.Error = error;
        //        return moveResponse;
        //    }
        //}

        // Search for file(s) or folders
        public FileExplorerResponse Search(string path, string searchString, bool showHiddenItems, bool caseSensitive, params FileExplorerDirectoryContent[] data)
        {
            throw new NotImplementedException();
            //directoryContentItems.Clear();
            //FileManagerResponse searchResponse = GetFiles(path, true, data);
            //directoryContentItems.AddRange(searchResponse.Files);
            //GetAllFiles(path, searchResponse);
            //searchResponse.Files = directoryContentItems.Where(item => new Regex(WildcardToRegex(searchString), caseSensitive ? RegexOptions.None : RegexOptions.IgnoreCase).IsMatch(item.Name));
            //return searchResponse;
        }

        // Gets all files
        protected virtual void GetAllFiles(string path, bool ViewDeleted, FileExplorerResponse data)
        {
            FileExplorerResponse directoryList = new FileExplorerResponse();
            directoryList.Files = data.Files.Where(item => item.IsFile == false);
            for (int i = 0; i < directoryList.Files.Count(); i++)
            {
                FileExplorerResponse innerData = GetFiles(path + directoryList.Files.ElementAt(i).Name + "/", ViewDeleted, new[] { directoryList.Files.ElementAt(i) });
                innerData.Files = innerData.Files.Select(file => new FileExplorerDirectoryContent
                {
                    Name = file.Name,
                    Type = file.Type,
                    IsFile = file.IsFile,
                    Size = file.Size,
                    HasChild = file.HasChild,
                    FilterPath = file.FilterPath
                });
                directoryContentItems.AddRange(innerData.Files);
                GetAllFiles(path + directoryList.Files.ElementAt(i).Name + "/", ViewDeleted, innerData);
            }
        }

        protected virtual string WildcardToRegex(string pattern)
        {
            return "^" + Regex.Escape(pattern).Replace(@"\*", ".*").Replace(@"\?", ".") + "$";
        }

        public string ToCamelCase(object userData)
        {
            JsonSerializerOptions options = new JsonSerializerOptions
            {
                PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            };

            return JsonSerializer.Serialize(userData, options);
        }

        [GeneratedRegex("/?([^/]+)/?$")]
        private static partial Regex ExtractNameRegex();
    }
}