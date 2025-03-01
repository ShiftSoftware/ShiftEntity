using Azure.Storage.Blobs;
using Microsoft.Azure.Functions.Worker;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats.Png;
using SixLabors.ImageSharp.Processing;
using Image = SixLabors.ImageSharp.Image;

namespace ShiftSoftware.ShiftEntity.Functions.FileExplorer;

public class ImageOperations
{
    private static List<ResizeOptions> Sizes =
    [
        new() {
            Size = new Size(1000, 1000),
            Mode = ResizeMode.Max,
        },
        new() {
            Size = new Size(500, 500),
            Mode = ResizeMode.Max,
        },
        new() {
            Size = new Size(250, 250),
            Mode = ResizeMode.Max,
        },
    ];

    [Function(nameof(CreateThumbnailOnFileChanged))]
    [BlobOutput("%ThumbnailContainerName%/{name}.png")]
    public static void CreateThumbnailOnFileChanged(
        [BlobTrigger("%DefaultContainerName%/{name}.{extension}")] BlobClient blob,
        [BlobInput("%ThumbnailContainerName%")] BlobContainerClient container,
        string name)
    {
        var stream = blob.OpenRead();
            
        using (var image = Image.Load(stream))
        {
            foreach (var size in Sizes)
            {
                var imageSmall = new MemoryStream();
                image.Mutate(C => C.Resize(size));
                image.SaveAsPng(imageSmall);

                imageSmall.Position = 0;
                container.UploadBlob($"{blob.AccountName}_{blob.BlobContainerName}/{name}_{size.Size.Width}x{size.Size.Height}.png", imageSmall);

                imageSmall.Dispose();
            }
        }
    }
}
