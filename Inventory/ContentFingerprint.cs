using System.Buffers;
using System.Security.Cryptography;

namespace FilesizeChecker.Inventory;

internal static class ContentFingerprint
{
    public static string HashAndRewind(FileStream stream, CancellationToken token)
    {
        byte[] buffer = ArrayPool<byte>.Shared.Rent(64 * 1024);
        try
        {
            using IncrementalHash hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            while (true)
            {
                token.ThrowIfCancellationRequested();
                int count = stream.Read(buffer);
                if (count == 0)
                    break;
                hash.AppendData(buffer.AsSpan(0, count));
            }
            stream.Position = 0;
            return Convert.ToHexString(hash.GetHashAndReset());
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer, clearArray: true);
        }
    }
}
