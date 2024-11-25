using Microsoft.AspNetCore.Mvc;
using ShiftSoftware.ShiftEntity.Core.Services;
using ShiftSoftware.ShiftEntity.Model.Dtos;
using System;
using System.Net.Http;
using System.Net.Http.Json;
using System.Threading.Tasks;
using Microsoft.Extensions.Configuration;
using ShiftSoftware.ShiftEntity.Web.Services;

namespace ShiftSoftware.ShiftEntity.Web.Controllers;

[Route("api/[controller]")]
[ApiController]
public class FileExplorerController : ControllerBase
{
    private readonly AzureStorageService azureStorageService;
    private readonly IFileExplorerAccessControl? fileExplorerAccessControl;
    private HttpClient httpClient;
    private string AzureFunctionsEndpoint;
    private AzureFileProvider operation;

    [Obsolete]
    public FileExplorerController(AzureStorageService azureStorageService, HttpClient httpClient, IConfiguration configuration, IFileExplorerAccessControl? fileExplorerAccessControl = null)
    {
        this.httpClient = httpClient;
        this.azureStorageService = azureStorageService;
        this.operation = new AzureFileProvider(azureStorageService, fileExplorerAccessControl);

        // temp
        var endpoint = configuration.GetValue<string>("AzureFunctions:Endpoint");
        if (string.IsNullOrWhiteSpace(endpoint))
        {
            throw new ArgumentNullException("AzureFunctions:Endpoint not found in appsettings.json");
        }
        this.AzureFunctionsEndpoint = endpoint;

        this.fileExplorerAccessControl = fileExplorerAccessControl;
    }

    [HttpPost]
    [Route("FileOperations")]
    public object FileOperations([FromBody] FileExplorerDirectoryContent args)
    {
        object? RootDir = null;
        args.CustomData?.TryGetValue("RootDir", out RootDir);
        this.operation.SetRootDirectory(RootDir?.ToString() ?? string.Empty);

        if (args.Path != "")
        {
            string startPath = this.operation.blobPath;
            string originalPath = (this.operation.filesPath).Replace(startPath, "");
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
                return this.operation.ToCamelCase(this.operation.GetFiles(args.Path, args.ShowHiddenItems, args.Data));
            case "delete":
                // Deletes the selected file(s) or folder(s) from the given path.
                return this.operation.ToCamelCase(this.operation.Delete(args.Path, args.Names, softDelete: true, args.Data));
            case "details":
                // Gets the details of the selected file(s) or folder(s).
                return this.operation.ToCamelCase(this.operation.Details(args.Path, args.Names, args.Data));
            case "create":
                // Creates a new folder in a given path.
                return this.operation.ToCamelCase(this.operation.Create(args.Path, args.Name, args.Data));
            case "search":
                // Gets the list of file(s) or folder(s) from a given path based on the searched key string.
                return this.operation.ToCamelCase(this.operation.Search(args.Path, args.SearchString, args.ShowHiddenItems, args.CaseSensitive, args.Data));
            case "rename":
                // Renames a file or folder.
                return this.operation.ToCamelCase(this.operation.Rename(args.Path, args.Name, args.NewName, false, args.ShowFileExtension, args.Data));
            case "copy":
                // Copies the selected file(s) or folder(s) from a path and then pastes them into a given target path.
                return this.operation.ToCamelCase(this.operation.Copy(args.Path, args.TargetPath, args.Names, args.RenameFiles, args.TargetData, args.Data));
            case "move":
                // Cuts the selected file(s) or folder(s) from a path and then pastes them into a given target path.
                return this.operation.ToCamelCase(this.operation.Move(args.Path, args.TargetPath, args.Names, args.RenameFiles, args.TargetData, args.Data));
        }

        return null;
    }

    [HttpPost("ZipFiles")]
    public async Task<ActionResult> ZipFiles(ZipOptionsDTO zipOptions)
    {
        var res = await httpClient.PostAsJsonAsync(AzureFunctionsEndpoint + "/api/zip", zipOptions);
        return StatusCode((int)res.StatusCode);
    }

    [HttpPost("UnzipFiles")]
    public async Task<ActionResult> UnzipFiles(ZipOptionsDTO zipOptions)
    {
        var res = await httpClient.PostAsJsonAsync(AzureFunctionsEndpoint+ "/api/unzip", zipOptions);
        return StatusCode((int)res.StatusCode);
    }


    //private void EnsureRootDirExists()
    //{
    //    var blob = azureStorageService.blobServiceClients[AzureAccount.AccountName];

    //    // create an empty dir if the root dir doesn't exist
    //    var rootDirItem = blob.GetBlobContainerClient(AzureAccount.DefaultContainerName).GetBlobClient(rootDir + "/" + Constants.FileManagerHiddenFilename);
    //    if (!rootDirItem.Exists())
    //    {
    //        rootDirItem.Upload(new MemoryStream());
    //    }
    //}

}