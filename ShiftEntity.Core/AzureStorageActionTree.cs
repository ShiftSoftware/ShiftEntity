using ShiftSoftware.TypeAuth.Core;
using ShiftSoftware.TypeAuth.Core.Actions;

namespace ShiftSoftware.ShiftEntity.Core;

[ActionTree("Azure Storage", "Azure Storage")]
public class AzureStorageActionTree
{
    public readonly static DecimalAction MaxUploadSizeInMegaBytes = new DecimalAction("Max Upload Size", null, 0, 100m);
    public readonly static ReadWriteDeleteAction UploadFiles = new ReadWriteDeleteAction("Upload Files");
}