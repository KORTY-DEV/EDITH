using Edith.Core.Files;
using Microsoft.VisualBasic.FileIO;

namespace Edith.App;

/// <summary>The real recycle bin: the file can be restored from it, nothing is erased.</summary>
public sealed class WindowsRecycleBin : IRecycleBin
{
    public void Recycle(string path) =>
        FileSystem.DeleteFile(path, UIOption.OnlyErrorDialogs, RecycleOption.SendToRecycleBin, UICancelOption.ThrowException);
}
