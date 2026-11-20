using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace FilesizeChecker.Inventory;

internal readonly record struct FileIdentity(ulong Volume, ulong IdLow, ulong IdHigh);

internal sealed record WindowsFileStamp(FileIdentity Identity, long Bytes, long Created, long Written,
    long Changed, uint Links)
{
    public bool CanCache => Changed > 0;

    public static WindowsFileStamp? Read(FileStream stream)
    {
        SafeFileHandle handle = stream.SafeFileHandle;
        if (!GetIdentity(handle, InformationClass.Id, out NativeIdentity identity, (uint)Marshal.SizeOf<NativeIdentity>())
            || (identity.Low == 0 && identity.High == 0)
            || !GetBasic(handle, InformationClass.Basic, out BasicInfo basic, (uint)Marshal.SizeOf<BasicInfo>())
            || !GetStandard(handle, InformationClass.Standard, out StandardInfo standard, (uint)Marshal.SizeOf<StandardInfo>()))
            return null;

        return new WindowsFileStamp(new FileIdentity(identity.Volume, identity.Low, identity.High),
            standard.EndOfFile, basic.CreationTime, basic.LastWriteTime, basic.ChangeTime, standard.NumberOfLinks);
    }

    private enum InformationClass { Basic = 0, Standard = 1, Id = 18 }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeIdentity
    {
        public ulong Volume;
        public ulong Low;
        public ulong High;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BasicInfo
    {
        public long CreationTime;
        public long LastAccessTime;
        public long LastWriteTime;
        public long ChangeTime;
        public uint FileAttributes;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct StandardInfo
    {
        public long AllocationSize;
        public long EndOfFile;
        public uint NumberOfLinks;
        public byte DeletePending;
        public byte Directory;
    }

    [DllImport("kernel32.dll", EntryPoint = "GetFileInformationByHandleEx")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetIdentity(SafeFileHandle handle, InformationClass infoClass, out NativeIdentity info, uint size);

    [DllImport("kernel32.dll", EntryPoint = "GetFileInformationByHandleEx")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetBasic(SafeFileHandle handle, InformationClass infoClass, out BasicInfo info, uint size);

    [DllImport("kernel32.dll", EntryPoint = "GetFileInformationByHandleEx")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetStandard(SafeFileHandle handle, InformationClass infoClass, out StandardInfo info, uint size);
}
