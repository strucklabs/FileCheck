using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace FilesizeChecker.Inventory;

internal static class WindowsFileOperations
{
    private const uint GenericRead = 0x80000000;
    private const uint GenericWrite = 0x40000000;
    private const uint DeleteAccess = 0x00010000;
    private const uint ReadAttributes = 0x80;
    private const uint WriteAttributes = 0x100;
    private const uint AddFile = 2;
    private const uint AddDirectory = 4;
    private const uint ShareRead = 1;
    private const uint ShareWrite = 2;
    private const uint ShareDelete = 4;
    private const uint OpenExisting = 3;
    private const uint BackupSemantics = 0x02000000;
    private const uint OpenReparsePoint = 0x00200000;
    private const uint CopyFailIfExists = 1;
    private const uint RenameInfo = 3;
    private const uint DispositionInfo = 4;
    private const uint DispositionInfoEx = 21;

    public static SafeFileHandle OpenFile(string path, bool delete = false)
    {
        VerifyParents(path);
        SafeFileHandle handle = Open(path, GenericRead | (delete ? DeleteAccess | WriteAttributes : 0), ShareRead, OpenReparsePoint);
        try
        {
            WindowsFileStamp stamp = ReadStamp(handle, path);
            if ((stamp.Attributes & (FileAttributes.Directory | FileAttributes.ReparsePoint)) != 0)
                throw new IOException($"Dateiverknüpfung oder Ordner kann nicht ausgeführt werden: {path}");
            return handle;
        }
        catch
        {
            handle.Dispose();
            throw;
        }
    }

    public static WindowsFileStamp ReadStamp(SafeFileHandle handle, string path) => WindowsFileStamp.Read(handle)
        ?? throw new IOException($"Eindeutige Dateikennung konnte nicht gelesen werden: {path}");

    public static WindowsFileStamp? Probe(string path)
    {
        try
        {
            using SafeFileHandle handle = OpenFile(path);
            return ReadStamp(handle, path);
        }
        catch (FileNotFoundException) { return null; }
        catch (DirectoryNotFoundException) { return null; }
    }

    public static WindowsFileStamp Require(SafeFileHandle handle, string path, WindowsFileStamp expected,
        bool renamed = false)
    {
        WindowsFileStamp actual = ReadStamp(handle, path);
        if (renamed ? !MatchesRenamed(actual, expected) : actual != expected)
            throw new IOException($"Die Datei entspricht nicht mehr dem geprüften Stand: {path}");
        return actual;
    }

    public static bool MatchesRenamed(WindowsFileStamp? actual, WindowsFileStamp expected) => actual is not null
        && actual.Identity == expected.Identity && actual.Bytes == expected.Bytes
        && actual.Written == expected.Written && actual.Links == expected.Links
        && (actual.Attributes & ~FileAttributes.Archive) == (expected.Attributes & ~FileAttributes.Archive);

    public static bool Exists(string path)
    {
        try { _ = File.GetAttributes(path); return true; }
        catch (FileNotFoundException) { return false; }
        catch (DirectoryNotFoundException) { return false; }
    }

    public static void VerifyParents(string path)
    {
        for (string? folder = Path.GetDirectoryName(path); folder is not null; folder = Path.GetDirectoryName(folder))
        {
            if (!Exists(folder))
                continue;
            FileAttributes attributes = File.GetAttributes(folder);
            if (!attributes.HasFlag(FileAttributes.Directory) || attributes.HasFlag(FileAttributes.ReparsePoint))
                throw new IOException($"Ziel- oder Quellpfad enthält eine Datei oder Verknüpfung: {folder}");
        }
    }

    public static (ulong Volume, long Free, string Folder) CheckDestination(string path)
    {
        using SafeFileHandle handle = OpenAncestor(path, write: true, out string parent);
        WindowsFileStamp directory = ReadStamp(handle, parent);
        if (!GetDiskFreeSpaceEx(parent, out ulong available, out _, out _))
            throw Error(parent);
        return (directory.Identity.Volume, (long)Math.Min(available, long.MaxValue), parent);
    }

    public static ulong CheckVolume(string path)
    {
        using SafeFileHandle handle = OpenAncestor(path, write: false, out string parent);
        return ReadStamp(handle, parent).Identity.Volume;
    }

    private static SafeFileHandle OpenAncestor(string path, bool write, out string existing)
    {
        VerifyParents(path);
        string intended = Path.GetDirectoryName(path)!;
        string? parent = intended;
        while (parent is not null && !Exists(parent))
            parent = Path.GetDirectoryName(parent);
        if (parent is null)
            throw new IOException($"Ziellaufwerk nicht erreichbar: {intended}");

        uint access = ReadAttributes | (write ? (parent == intended ? AddFile : AddDirectory) : 0);
        SafeFileHandle handle = Open(parent, access, ShareRead | ShareWrite | ShareDelete, BackupSemantics | OpenReparsePoint);
        try
        {
            WindowsFileStamp directory = ReadStamp(handle, parent);
            if (!directory.Attributes.HasFlag(FileAttributes.Directory) || directory.Attributes.HasFlag(FileAttributes.ReparsePoint))
                throw new IOException($"Zielordner wurde geändert: {parent}");
            existing = parent;
            return handle;
        }
        catch
        {
            handle.Dispose();
            throw;
        }
    }

    public static void Rename(SafeFileHandle handle, string target)
    {
        VerifyParents(target);
        byte[] name = System.Text.Encoding.Unicode.GetBytes(ExtendedPath(target) + '\0');
        int offset = (int)Marshal.OffsetOf<NativeRenameInfo>(nameof(NativeRenameInfo.FirstCharacter));
        byte[] information = new byte[offset + name.Length];
        int lengthOffset = (int)Marshal.OffsetOf<NativeRenameInfo>(nameof(NativeRenameInfo.NameLength));
        BitConverter.TryWriteBytes(information.AsSpan(lengthOffset), name.Length - 2);
        name.CopyTo(information, offset);
        if (!SetFileInformationByHandle(handle, RenameInfo, information, (uint)information.Length))
            throw Error(target);
    }

    public static void Delete(SafeFileHandle handle, string path)
    {
        if (ReadStamp(handle, path).Attributes.HasFlag(FileAttributes.ReadOnly))
        {
            const uint deleteIgnoringReadOnly = 0x11;
            if (!SetFileInformationByHandle(handle, DispositionInfoEx, BitConverter.GetBytes(deleteIgnoringReadOnly), sizeof(uint)))
                throw Error(path);
            return;
        }
        if (!SetFileInformationByHandle(handle, DispositionInfo, [1], 1))
            throw Error(path);
    }

    public static WindowsFileStamp ConfirmRenamed(string path, WindowsFileStamp expected)
    {
        using (SafeFileHandle handle = OpenFile(path, delete: true))
        {
            WindowsFileStamp actual = Require(handle, path, expected, renamed: true);
            // NTFS kann bei erneut verwendeten Dateinamen die frühere Erstellungszeit einsetzen.
            if (actual.Created != expected.Created)
                RestoreCreationTime(handle, path, expected.Created);
        }
        using SafeFileHandle confirmed = OpenFile(path);
        return Require(confirmed, path, expected, renamed: true);
    }

    public static void RestoreCreationTime(SafeFileHandle handle, string path, long creation)
    {
        if (creation != 0 && !SetFileTime(handle, ref creation, IntPtr.Zero, IntPtr.Zero))
            throw Error(path);
    }

    public static void Copy(string source, string stage)
    {
        VerifyParents(stage);
        int cancel = 0;
        if (!CopyFileEx(ExtendedPath(source), ExtendedPath(stage), IntPtr.Zero, IntPtr.Zero, ref cancel, CopyFailIfExists))
            throw Error(stage);
    }

    public static long CopySpace(string path) => Streams(path).Sum(stream => checked((stream.Length + 65_535) / 65_536 * 65_536));

    public static long ContentBytes(string path) => Streams(path).Sum(stream => stream.Length);

    public static void FlushCopy(string path)
    {
        FileAttributes attributes = File.GetAttributes(path);
        bool readOnly = attributes.HasFlag(FileAttributes.ReadOnly);
        if (readOnly)
            File.SetAttributes(path, attributes & ~FileAttributes.ReadOnly);
        try
        {
            foreach (FileDataStream stream in Streams(path))
            {
                using SafeFileHandle handle = Open(path + stream.Suffix, GenericWrite | WriteAttributes, ShareRead, OpenReparsePoint);
                if (!FlushFileBuffers(handle))
                    throw Error(path + stream.Suffix);
            }
        }
        finally
        {
            if (readOnly)
                File.SetAttributes(path, attributes);
        }
    }

    public static bool SameContent(string left, string right, Action? confirmed = null)
    {
        FileDataStream[] first = Streams(left);
        FileDataStream[] second = Streams(right);
        if (!first.SequenceEqual(second))
            return false;

        var leftHandles = new List<FileStream>();
        var rightHandles = new List<FileStream>();
        try
        {
            foreach (FileDataStream stream in first)
            {
                leftHandles.Add(OpenDataStream(left + stream.Suffix));
                rightHandles.Add(OpenDataStream(right + stream.Suffix));
            }
            byte[] a = new byte[1024 * 1024];
            byte[] b = new byte[a.Length];
            for (int index = 0; index < leftHandles.Count; index++)
            {
                while (true)
                {
                    int count = leftHandles[index].ReadAtLeast(a, a.Length, false);
                    if (count != rightHandles[index].ReadAtLeast(b, b.Length, false) || !a.AsSpan(0, count).SequenceEqual(b.AsSpan(0, count)))
                        return false;
                    if (count < a.Length)
                        break;
                }
            }
            if (!first.SequenceEqual(Streams(left)) || !second.SequenceEqual(Streams(right)))
                return false;
            confirmed?.Invoke();
            return true;
        }
        finally
        {
            foreach (FileStream stream in leftHandles.Concat(rightHandles))
                stream.Dispose();
        }
    }

    private static FileStream OpenDataStream(string path) => new(
        Open(path, GenericRead, ShareRead | ShareDelete, OpenReparsePoint), FileAccess.Read);

    private sealed record FileDataStream(string Suffix, long Length);

    private static FileDataStream[] Streams(string path)
    {
        using SafeFindHandle handle = FindFirstStream(ExtendedPath(path), 0, out StreamData stream, 0);
        if (handle.IsInvalid)
        {
            int code = Marshal.GetLastPInvokeError();
            if (code is 38 or 50 or 87)
                return [new FileDataStream("", new FileInfo(path).Length)];
            throw Error(path, code);
        }

        var streams = new List<FileDataStream>();
        do
        {
            streams.Add(new FileDataStream(stream.Name == "::$DATA" ? "" : stream.Name, stream.Length));
        } while (FindNextStream(handle, out stream));
        int end = Marshal.GetLastPInvokeError();
        if (end != 38)
            throw Error(path, end);
        return streams.OrderBy(item => item.Suffix, StringComparer.Ordinal).ToArray();
    }

    private static SafeFileHandle Open(string path, uint access, uint share, uint flags)
    {
        SafeFileHandle handle = CreateFile(ExtendedPath(path), access, share, IntPtr.Zero, OpenExisting, flags, IntPtr.Zero);
        if (!handle.IsInvalid)
            return handle;
        int code = Marshal.GetLastPInvokeError();
        handle.Dispose();
        if (code == 2)
            throw new FileNotFoundException($"Datei nicht gefunden: {path}", path);
        if (code is 3 or 15)
            throw new DirectoryNotFoundException($"Ordner oder Laufwerk nicht gefunden: {path}");
        throw Error(path, code);
    }

    private static string ExtendedPath(string path) => path.StartsWith(@"\\?\", StringComparison.Ordinal) ? path
        : path.StartsWith(@"\\", StringComparison.Ordinal) ? @"\\?\UNC\" + path[2..] : @"\\?\" + path;

    private static IOException Error(string path, int? error = null)
    {
        var detail = new Win32Exception(error ?? Marshal.GetLastPInvokeError());
        return new IOException($"{detail.Message} · {path}", detail);
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NativeRenameInfo
    {
        public uint Flags;
        public IntPtr RootDirectory;
        public uint NameLength;
        public char FirstCharacter;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct StreamData
    {
        public long Length;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 296)]
        public string Name;
    }

    private sealed class SafeFindHandle() : SafeHandleZeroOrMinusOneIsInvalid(true)
    {
        protected override bool ReleaseHandle() => FindClose(handle);
    }

    [DllImport("kernel32.dll", EntryPoint = "CreateFileW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFile(string name, uint access, uint share, IntPtr security, uint mode,
        uint flags, IntPtr template);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetFileInformationByHandle(SafeFileHandle handle, uint kind, byte[] information, uint size);

    [DllImport("kernel32.dll", EntryPoint = "GetDiskFreeSpaceExW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetDiskFreeSpaceEx(string directory, out ulong available, out ulong total, out ulong free);

    [DllImport("kernel32.dll", EntryPoint = "CopyFileExW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CopyFileEx(string source, string destination, IntPtr progress, IntPtr data, ref int cancel, uint flags);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool FlushFileBuffers(SafeFileHandle file);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetFileTime(SafeFileHandle file, ref long creation, IntPtr lastAccess, IntPtr lastWrite);

    [DllImport("kernel32.dll", EntryPoint = "FindFirstStreamW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFindHandle FindFirstStream(string name, int level, out StreamData data, uint flags);

    [DllImport("kernel32.dll", EntryPoint = "FindNextStreamW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool FindNextStream(SafeFindHandle search, out StreamData data);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool FindClose(IntPtr handle);
}
