using System.Runtime.InteropServices;

namespace ZeiLand.Native;

/// <summary>The user's folders wherever they have been moved (Downloads on another drive, and so on).</summary>
internal static class KnownFolders
{
    public static readonly Guid Downloads = new("374de290-123f-4565-9164-39c4925e467b");
    public static readonly Guid Screenshots = new("b7bede81-df94-4682-a7d8-57a52620b86f"); // Pictures\Screenshots

    /// <summary>The folder's path; null if it has none.</summary>
    public static string? Get(Guid id)
    {
        if (SHGetKnownFolderPath(id, 0, IntPtr.Zero, out var pointer) != 0) return null;
        try
        {
            return Marshal.PtrToStringUni(pointer);
        }
        finally
        {
            Marshal.FreeCoTaskMem(pointer);
        }
    }

    [DllImport("shell32.dll")]
    private static extern int SHGetKnownFolderPath([MarshalAs(UnmanagedType.LPStruct)] Guid id, uint flags, IntPtr token, out IntPtr path);
}
