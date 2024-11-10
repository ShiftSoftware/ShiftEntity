﻿using System;
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
using Syncfusion.EJ2.FileManager.Base;
using System.Text;
using Azure.Storage.Blobs.Specialized;
using System.Text.Json;
using Azure;
using ShiftSoftware.ShiftEntity.Core;
using ShiftSoftware.ShiftEntity.Core.Services;
using Azure.Storage.Sas;

namespace ShiftSoftware.ShiftEntity.Web.Services
{
    public class AzureFileProvider : IAzureFileProviderBase
    {
        List<FileManagerDirectoryContent> directoryContentItems = new List<FileManagerDirectoryContent>();
        BlobContainerClient container;
        public string blobPath;
        public string filesPath;
        long size;
        static string rootPath;
        List<string> existFiles = new List<string>();
        List<string> missingFiles = new List<string>();
        bool isFolderAvailable = false;
        List<FileManagerDirectoryContent> copiedFiles = new List<FileManagerDirectoryContent>();
        DateTime lastUpdated = DateTime.MinValue;
        DateTime prevUpdated = DateTime.MinValue;

        private AzureStorageService? azureStorageService;
        private readonly string rootDir;

        private readonly IFileExplorerAccessControl? fileExplorerAccessControl;

        public AzureFileProvider(AzureStorageService azureStorageService, string rootDir, IFileExplorerAccessControl? fileExplorerAccessControl)
        {
            this.azureStorageService = azureStorageService;
            this.rootDir = rootDir;

            var accountName = azureStorageService.GetDefaultAccountName();
            var containerName = azureStorageService.GetDefaultContainerName(accountName);
            var client = azureStorageService.blobServiceClients[accountName];
            var azureAccount = azureStorageService.azureStorageAccounts[accountName];
            container = client.GetBlobContainerClient(containerName);

            blobPath = azureAccount.EndPoint.Trim(['/', '\\']) + "/" + containerName.Trim(['/', '\\']) + "/";
            filesPath = blobPath + rootDir.Trim(['/', '\\']);
            blobPath = blobPath.Replace("../", "");
            filesPath = filesPath.Replace("../", "");

            SetBlobContainer(blobPath, filesPath);
            this.fileExplorerAccessControl = fileExplorerAccessControl;
        }


        // Registering the azure storage 
        public void RegisterAzure(string accountName, string accountKey, string blobName)
        {
            container = new BlobServiceClient(new Uri(blobPath.Substring(0, blobPath.Length - blobName.Length - 1)), new StorageSharedKeyCredential(accountName, accountKey), null).GetBlobContainerClient(blobName);

        }

        // Sets blob and file path
        public void SetBlobContainer(string blob_Path, string file_Path)
        {
            blobPath = blob_Path;
            filesPath = file_Path;
            rootPath = filesPath.Replace(blobPath, "");
        }

        // Reads the storage 
        public FileManagerResponse GetFiles(string path, bool showHiddenItems, FileManagerDirectoryContent[] selectedItems)
        {
            return GetFilesAsync(path, "*.*", selectedItems).GetAwaiter().GetResult();
        }

        // Reads the storage files
        protected async Task<FileManagerResponse> GetFilesAsync(string path, string filter, FileManagerDirectoryContent[] selectedItems)
        {
            FileManagerResponse readResponse = new FileManagerResponse();
            List<string> prefixes = new List<string>();
            List<FileManagerDirectoryContent> details = new List<FileManagerDirectoryContent>();
            FileManagerDirectoryContent cwd = new FileManagerDirectoryContent();
            try
            {
                // Check if there are any items in this dir or any sub dirs
                var blobPages = container.GetBlobsAsync(prefix: path).AsPages(pageSizeHint: 100).GetAsyncEnumerator();
                await blobPages.MoveNextAsync();

                if (!blobPages.Current.Values.Any())
                {
                    ErrorDetails errorDetails = new ErrorDetails();
                    errorDetails.Message = "Could not find a part of the path '" + path + "'.";
                    errorDetails.Code = "417";
                    readResponse.Error = errorDetails;
                    return readResponse;
                }

                string[] extensions = (filter.Replace(" ", "") ?? "*").Split(",|;".ToCharArray(), StringSplitOptions.RemoveEmptyEntries);
                var noFilter = extensions[0].Equals("*.*") || extensions[0].Equals("*");
                cwd.Name = selectedItems.Length != 0 ? selectedItems[0].Name : path.TrimEnd('/');
                container.GetBlobClient(path);
                cwd.Type = "File Folder";
                cwd.FilterPath = selectedItems.Length != 0 ? selectedItems[0].FilterPath : "";
                cwd.Size = 0;

                foreach (Page<BlobHierarchyItem> page in container.GetBlobsByHierarchy(delimiter: "/", prefix: path).AsPages())
                {
                    
                    foreach (BlobItem item in page.Values.Where(x => x.IsBlob).Select(x => x.Blob))
                    {
                        //var isHidden = item.Metadata.TryGetValue(Constants.FileManagerHiddenMetadataKey, out _);
                        var isHiddenEmptyFile = item.Name.EndsWith(Constants.FileExplorerHiddenFilename);
                        var fileTypeIsFiltered = Array.IndexOf(extensions, "*." + item.Name.ToString().Trim().Split('.')[item.Name.ToString().Trim().Split('.').Length - 1]) < 0;
                        var skip = (!noFilter && fileTypeIsFiltered) || isHiddenEmptyFile;// || isHidden;
                        if (skip) continue;

                        FileManagerDirectoryContent entry = new FileManagerDirectoryContent();
                        entry.Name = item.Name.Replace(path, "");
                        entry.Path = item.Name;
                        entry.Type = Path.GetExtension(item.Name.Replace(path, ""));
                        entry.IsFile = true;
                        entry.Size = item.Properties.ContentLength.Value;
                        entry.DateModified = item.Properties.LastModified.Value.LocalDateTime;
                        entry.HasChild = false;
                        entry.FilterPath = selectedItems.Length != 0 ? path.Replace(rootPath, "") : "/";

                        entry.TargetPath = azureStorageService?.GetSignedURL(rootDir + entry.FilterPath + entry.Name, BlobSasPermissions.Read, container.Name);

                        details.Add(entry);
                    }


                    foreach (string item in page.Values.Where(x => x.IsPrefix).Select(x => x.Prefix))
                    {
                        FileManagerDirectoryContent entry = new FileManagerDirectoryContent();
                        string dir = item;
                        entry.Name = dir.Replace(path, "").Replace("/", "");
                        entry.Type = "Directory";
                        entry.IsFile = false;
                        entry.Size = 0;
                        entry.Path = item;
                        entry.HasChild = false;
                        entry.FilterPath = selectedItems.Length != 0 ? path.Replace(rootPath, "") : "/";
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
            catch (Exception)
            {
                return readResponse;
            }

            readResponse.Files = details;
            return readResponse;
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
        public FileManagerResponse Details(string path, string[] names, params FileManagerDirectoryContent[] data)
        {
            return GetDetailsAsync(path, names, data).GetAwaiter().GetResult();
        }

        // Gets the details
        protected async Task<FileManagerResponse> GetDetailsAsync(string path, string[] names, IEnumerable<object> selectedItems = null)
        {
            bool isVariousFolders = false;
            string previousPath = "";
            string previousName = "";
            FileManagerResponse detailsResponse = new FileManagerResponse();
            try
            {
                bool isFile = false;
                bool namesAvailable = names.Length > 0;
                if (names.Length == 0 && selectedItems != null)
                {
                    List<string> values = new List<string>();
                    foreach (FileManagerDirectoryContent item in selectedItems)
                    {
                        values.Add(item.Name);
                    }
                    names = values.ToArray();
                }
                FileDetails fileDetails = new FileDetails();
                long multipleSize = 0;
                if (selectedItems != null)
                {
                    foreach (FileManagerDirectoryContent fileItem in selectedItems)
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
                                fileDetails.Size = ByteConversion(sizeValue); fileDetails.Modified = await DirectoryLastModified(path); detailsResponse.Details = fileDetails;
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
        public FileManagerResponse Create(string path, string name, params FileManagerDirectoryContent[] selectedItems)
        {
            isFolderAvailable = false;
            FileManagerResponse createResponse = new FileManagerResponse();
            try
            {
                CreateFolderAsync(path, name, selectedItems).GetAwaiter().GetResult();
                if (!isFolderAvailable)
                {
                    FileManagerDirectoryContent content = new FileManagerDirectoryContent();
                    content.Name = name;
                    FileManagerDirectoryContent[] directories = new[] { content };
                    createResponse.Files = (IEnumerable<FileManagerDirectoryContent>)directories;
                }
                else
                {
                    ErrorDetails error = new ErrorDetails();
                    error.FileExists = existFiles;
                    error.Code = "400";
                    error.Message = "Folder Already Exists";
                    createResponse.Error = error;
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
        public FileManagerResponse Rename(string path, string oldName, string newName, bool replace = false, bool showFileExtension = true, params FileManagerDirectoryContent[] data)
        {
            throw new NotImplementedException();
            //return RenameAsync(path, oldName, newName, showFileExtension, data).GetAwaiter().GetResult();
        }

        // Renames file(s) or folder(s)
        protected async Task<FileManagerResponse> RenameAsync(string path, string oldName, string newName, bool showFileExtension, params FileManagerDirectoryContent[] selectedItems)
        {
            FileManagerResponse renameResponse = new FileManagerResponse();
            List<FileManagerDirectoryContent> details = new List<FileManagerDirectoryContent>();
            FileManagerDirectoryContent entry = new FileManagerDirectoryContent();
            try
            {
                bool isAlreadyAvailable = false;
                bool isFile = false;
                foreach (FileManagerDirectoryContent fileItem in selectedItems)
                {
                    FileManagerDirectoryContent directoryContent = fileItem;
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
        public FileManagerResponse Delete(string path, string[] names, params FileManagerDirectoryContent[] data)
        {
            var newData = new List<FileManagerDirectoryContent>();

            if (this.fileExplorerAccessControl is not null)
            {
                newData = this.fileExplorerAccessControl.FilterWithDeleteAccess(data);
            }
            else
            {
                newData = data.ToList();
            }

            return RemoveAsync(names, path, newData).GetAwaiter().GetResult();
        }

        // Deletes file(s) or folder(s)
        protected async Task<FileManagerResponse> RemoveAsync(string[] names, string path, List<FileManagerDirectoryContent> selectedItems)
        {
            FileManagerResponse removeResponse = new FileManagerResponse();
            List<FileManagerDirectoryContent> details = new List<FileManagerDirectoryContent>();
            FileManagerDirectoryContent entry = new FileManagerDirectoryContent();
            try
            {
                foreach (FileManagerDirectoryContent fileItem in selectedItems)
                {
                    if (fileItem.IsFile)
                    {
                        path = filesPath.Replace(blobPath, "") + fileItem.FilterPath;
                        BlobClient currentFile = container.GetBlobClient(path + fileItem.Name);
                        currentFile.DeleteIfExists();
                        string absoluteFilePath = Path.Combine(Path.GetTempPath(), fileItem.Name);
                        DirectoryInfo tempDirectory = new DirectoryInfo(Path.GetTempPath());
                        foreach (string file in Directory.GetFiles(tempDirectory.ToString()))
                        {
                            if (file.ToString() == absoluteFilePath)
                            {
                                File.Delete(file);
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
                    else
                    {
                        path = filesPath.Replace(blobPath, "") + fileItem.FilterPath;
                        foreach (Page<BlobItem> items in container.GetBlobs(prefix: path + fileItem.Name + "/").AsPages())
                        {
                            foreach (BlobItem item in items.Values)
                            {
                                BlobClient currentFile = container.GetBlobClient(item.Name);
                                await currentFile.DeleteAsync();
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
        private static FileManagerDirectoryContent GetFileDetails(string targetPath, FileManagerDirectoryContent fileDetails)
        {
            FileManagerDirectoryContent entry = new FileManagerDirectoryContent();
            entry.Name = fileDetails.Name;
            entry.Type = fileDetails.Type;
            entry.IsFile = fileDetails.IsFile;
            entry.Size = fileDetails.Size;
            entry.HasChild = fileDetails.HasChild;
            entry.FilterPath = targetPath.Replace(rootPath, "");
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
        public FileManagerResponse Copy(string path, string targetPath, string[] names, string[] renameFiles, FileManagerDirectoryContent targetData, params FileManagerDirectoryContent[] data)
        {
            throw new NotImplementedException();
            //return CopyToAsync(path, targetPath, names, renameFiles, data).GetAwaiter().GetResult();
        }

        //private async Task<FileManagerResponse> CopyToAsync(string path, string targetPath, string[] names, string[] renamedFiles = null, params FileManagerDirectoryContent[] data)
        //{
        //    FileManagerResponse copyResponse = new FileManagerResponse();
        //    HashSet<string> processedItems = new HashSet<string>();
        //    try
        //    {
        //        renamedFiles = renamedFiles ?? Array.Empty<string>();
        //        foreach (FileManagerDirectoryContent item in data)
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
        //private void CopySubFolder(FileManagerDirectoryContent subFolder, string targetPath)
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
        //            FileManagerDirectoryContent itemDetail = new FileManagerDirectoryContent();
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

        //private async void MoveSubFolder(FileManagerDirectoryContent subFolder, string targetPath)
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
        //            FileManagerDirectoryContent itemDetail = new FileManagerDirectoryContent();
        //            itemDetail.Name = item.Replace(subFolder.Path, "").Replace("/", "");
        //            itemDetail.Path = subFolder.Path + "/" + itemDetail.Name;
        //            MoveSubFolder(itemDetail, targetPath);
        //        }
        //    }
        //}

        // Moves file(s) or folders
        public FileManagerResponse Move(string path, string targetPath, string[] names, string[] renameFiles, FileManagerDirectoryContent targetData, params FileManagerDirectoryContent[] data)
        {
            throw new NotImplementedException();
            //return MoveToAsync(path, targetPath, names, renameFiles, data).GetAwaiter().GetResult();
        }

        //private async Task<FileManagerResponse> MoveToAsync(string path, string targetPath, string[] names, string[] renamedFiles = null, params FileManagerDirectoryContent[] data)
        //{
        //    FileManagerResponse moveResponse = new FileManagerResponse();
        //    try
        //    {
        //        renamedFiles = renamedFiles ?? Array.Empty<string>();
        //        foreach (FileManagerDirectoryContent item in data)
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
        public FileManagerResponse Search(string path, string searchString, bool showHiddenItems, bool caseSensitive, params FileManagerDirectoryContent[] data)
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
        protected virtual void GetAllFiles(string path, FileManagerResponse data)
        {
            FileManagerResponse directoryList = new FileManagerResponse();
            directoryList.Files = data.Files.Where(item => item.IsFile == false);
            for (int i = 0; i < directoryList.Files.Count(); i++)
            {
                FileManagerResponse innerData = GetFiles(path + directoryList.Files.ElementAt(i).Name + "/", true, new[] { directoryList.Files.ElementAt(i) });
                innerData.Files = innerData.Files.Select(file => new FileManagerDirectoryContent
                {
                    Name = file.Name,
                    Type = file.Type,
                    IsFile = file.IsFile,
                    Size = file.Size,
                    HasChild = file.HasChild,
                    FilterPath = file.FilterPath
                });
                directoryContentItems.AddRange(innerData.Files);
                GetAllFiles(path + directoryList.Files.ElementAt(i).Name + "/", innerData);
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

        public FileManagerResponse Upload(string path, IList<IFormFile> files, string action, params FileManagerDirectoryContent[] data)
        {
            throw new NotImplementedException();
        }

        public virtual FileStreamResult Download(string path, string[] names = null, params FileManagerDirectoryContent[] selectedItems)
        {
            throw new NotImplementedException();
        }

        public FileStreamResult GetImage(string path, string id, bool allowCompress, ImageSize size, params FileManagerDirectoryContent[] data)
        {
            throw new NotImplementedException();
        }

    }
}