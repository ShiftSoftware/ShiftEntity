using Azure.Storage.Blobs.Models;
using Azure.Storage.Blobs;
using Azure;
using Azure.Storage.Sas;
using Microsoft.AspNetCore.Mvc;
using ShiftSoftware.ShiftEntity.Core;
using ShiftSoftware.ShiftEntity.Core.Services;
using ShiftSoftware.ShiftEntity.Model;
using ShiftSoftware.ShiftEntity.Model.Dtos;
using ShiftSoftware.TypeAuth.AspNetCore;
using ShiftSoftware.TypeAuth.Core;
using Syncfusion.EJ2.FileManager.AzureFileProvider;
using Syncfusion.EJ2.Linq;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Http;

namespace ShiftSoftware.ShiftEntity.Web.Controllers;

[Route("api/[controller]")]
[ApiController]
public class FileManagerController : ControllerBase
{

    public AzureFileProvider operation;
    private AzureStorageService azureStorageService;
    private AzureStorageOption AzureAccount;

    public string blobPath { get; set; }
    public string filePath { get; set; }

    private string rootDir = "FileManager";

    [Obsolete]
    public FileManagerController(AzureStorageService azureStorageService)
    {
        this.azureStorageService = azureStorageService;
        this.operation = new AzureFileProvider();

        var accountName = azureStorageService.GetDefaultAccountName();
        AzureAccount = azureStorageService.azureStorageAccounts[accountName];
        var blob = azureStorageService.blobServiceClients[accountName];

        blobPath = AzureAccount.EndPoint.Trim(['/', '\\']) + "/" + AzureAccount.DefaultContainerName.Trim(['/', '\\']) + "/";
        filePath = blobPath + rootDir.Trim(['/', '\\']);
        blobPath = blobPath.Replace("../", "");
        filePath = filePath.Replace("../", "");

        // create an empty dir if the root dir doesn't exist
        var rootDirItem = blob.GetBlobContainerClient(AzureAccount.DefaultContainerName).GetBlobClient(rootDir + "/About.txt");
        if (!rootDirItem.Exists())
        {
            rootDirItem.Upload(new MemoryStream());
        }

        this.operation.SetBlobContainer(blobPath, filePath);
        this.operation.RegisterAzure(accountName, AzureAccount.AccountKey, AzureAccount.DefaultContainerName);

    }

    [Route("FileOperations")]
    public object GenerateFileUploadSAS([FromBody] Syncfusion.EJ2.FileManager.Base.FileManagerDirectoryContent args)
    {
        if (args.Path != "")
        {
            string startPath = blobPath;
            string originalPath = (filePath).Replace(startPath, "");
            //-----------------
            //For example
            //string startPath = "https://azure_service_account.blob.core.windows.net/files/";
            //string originalPath = ("https://azure_service_account.blob.core.windows.net/files/Files").Replace(startPath, "");
            //-------------------
            args.Path = !args.Path.Contains(originalPath) ? (originalPath + args.Path).Replace("//", "/") : (args.Path).Replace("//", "/");
            args.TargetPath = (originalPath + args.TargetPath).Replace("//", "/");
        }

        switch (args.Action)
        {

            case "read":
                // Reads the file(s) or folder(s) from the given path.
                var response = this.operation.GetFiles(args.Path, args.ShowHiddenItems, args.Data);
                response.Files = response.Files.Where(x => x.Name != "About.txt");

                try
                {
                    response.Files.Where(x => x.IsFile).ForEach(x => x.TargetPath = azureStorageService.GetSignedURL(rootDir + x.FilterPath + x.Name, BlobSasPermissions.Read, AzureAccount.DefaultContainerName));
                }
                catch { }


                return this.ToCamelCase(response);
            case "delete":
                // Deletes the selected file(s) or folder(s) from the given path.
                return this.ToCamelCase(this.operation.Delete(args.Path, args.Names, args.Data));
            case "details":
                // Gets the details of the selected file(s) or folder(s).
                return this.ToCamelCase(this.operation.Details(args.Path, args.Names, args.Data));
            case "create":
                // Creates a new folder in a given path.
                return this.ToCamelCase(this.operation.Create(args.Path, args.Name, args.Data));
            case "search":
                // Gets the list of file(s) or folder(s) from a given path based on the searched key string.
                return this.ToCamelCase(this.operation.Search(args.Path, args.SearchString, args.ShowHiddenItems, args.CaseSensitive, args.Data));
            case "rename":
                // Renames a file or folder.
                return this.ToCamelCase(this.operation.Rename(args.Path, args.Name, args.NewName, false, args.ShowFileExtension, args.Data));
            case "copy":
                // Copies the selected file(s) or folder(s) from a path and then pastes them into a given target path.
                return this.ToCamelCase(this.operation.Copy(args.Path, args.TargetPath, args.Names, args.RenameFiles, args.TargetData, args.Data));
            case "move":
                // Cuts the selected file(s) or folder(s) from a path and then pastes them into a given target path.
                return this.ToCamelCase(this.operation.Move(args.Path, args.TargetPath, args.Names, args.RenameFiles, args.TargetData, args.Data));
        }

        return null;
    }

    [HttpPost("AzureUpload")]
    public ActionResult AzureUpload(Syncfusion.EJ2.FileManager.Base.FileManagerDirectoryContent args)
    {
        if (args.Path != "")
        {
            string startPath = blobPath;
            string originalPath = (filePath).Replace(startPath, "");
            args.Path = (originalPath + args.Path).Replace("//", "/");
            //----------------------
            //For example
            //string startPath = "https://azure_service_account.blob.core.windows.net/files/";
            //string originalPath = ("https://azure_service_account.blob.core.windows.net/files/Files").Replace(startPath, "");
            //args.Path = (originalPath + args.Path).Replace("//", "/");
            //----------------------
        }
        var uploadResponse = operation.Upload(args.Path, args.UploadFiles, args.Action, args.Data);
        if (uploadResponse.Error != null)
        {
            Response.Clear();
            Response.ContentType = "application/json; charset=utf-8";
            Response.StatusCode = Convert.ToInt32(uploadResponse.Error.Code);
            Response.HttpContext.Features.Get<IHttpResponseFeature>().ReasonPhrase = uploadResponse.Error.Message;
        }
        return Ok();
    }

    public string ToCamelCase(object userData)
    {
        JsonSerializerOptions options = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        };
        return JsonSerializer.Serialize(userData, options);
    }
}