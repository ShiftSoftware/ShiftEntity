using Azure.Storage.Blobs;
using Microsoft.Azure.Functions.Worker;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats.Png;
using SixLabors.ImageSharp.Processing;
using Image = SixLabors.ImageSharp.Image;

namespace ShiftSoftware.ShiftEntity.Functions.FileExplorer;

public class ImageOperations
{

    [Function(nameof(CreateThumbnailOnFileChanged))]
    [BlobOutput("%ThumbnailContainerName%/{name}.png")]
    public static void CreateThumbnailOnFileChanged(
        [BlobTrigger("%DefaultContainerName%/{name}.{extension}")] BlobClient blob,
        [BlobInput("%ThumbnailContainerName%")] BlobContainerClient container,
        string name)
    {
        var stream = blob.OpenRead();
        
        var sizesString = blob.GetProperties().Value.Metadata["sizes"].Split("|");
        var sizes = sizesString.Select(s => s.Split("x")).Select(s => new Size(int.Parse(s[0]), int.Parse(s[1])));

        using (var image = Image.Load(stream))
        {
            foreach (var size in sizes)
            {
                var imageSmall = new MemoryStream();
                image.Mutate(C => C.Resize(new ResizeOptions
                {
                    Size = size,
                    Mode = ResizeMode.Max,
                }));
                image.SaveAsPng(imageSmall);

                imageSmall.Position = 0;
                container.UploadBlob($"{blob.AccountName}_{blob.BlobContainerName}/{name}_{size.Width}x{size.Height}.png", imageSmall);

                imageSmall.Dispose();
            }
        }
    }
}
