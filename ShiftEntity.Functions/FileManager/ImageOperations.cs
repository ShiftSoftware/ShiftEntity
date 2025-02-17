using Azure.Storage.Blobs;
using Microsoft.Azure.Functions.Worker;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Processing;
using Image = SixLabors.ImageSharp.Image;

namespace ShiftSoftware.ShiftEntity.Functions.FileExplorer;

public class ImageOperations
{

    [Function(nameof(CreateThumbnailOnFileChanged))]
    [BlobOutput("%ThumbnailContainerName%/{name}.png")]
    public static byte[]? CreateThumbnailOnFileChanged(
        [BlobTrigger("%DefaultContainerName%/{name}.{extension}")] Stream blob,
        [BlobInput("%ThumbnailContainerName%")] BlobContainerClient container,
        string name)
    {
        try
        {
            var imageSmall = new MemoryStream();

            using (var image = Image.Load(blob))
            {
                image.Mutate(C => C.Resize(new ResizeOptions()
                {
                    Size = new Size(250, 250),
                    Mode = ResizeMode.Max,
                }));
                image.SaveAsPng(imageSmall);
            }

            return imageSmall.ToArray();
        }
        catch (Exception)
        {
            return null;
        }
        
    }

}
