using ShiftSoftware.ShiftEntity.Model;
using ShiftSoftware.ShiftEntity.Web.Services;
using System;
using ShiftSoftware.ShiftEntity.Web.Explorer;

namespace Microsoft.Extensions.DependencyInjection;

public static class FileExplorerBuilderExtensions
{
    public static IServiceCollection AddFileExplorer(this IServiceCollection builder, Action<FileExplorerConfiguration> action)
    {
        
        var config = new FileExplorerConfiguration();
        action.Invoke(config);

        builder.AddOptions<FileExplorerConfiguration>().Configure(action.Invoke);

        if (config.FileExplorerService == FileExplorerService.AzureBlobStorage)
            builder.AddScoped<IFileProvider, BlobStorageFileProvider>();

        return builder;
    }
}
