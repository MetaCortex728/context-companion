namespace ContextCompanion.Core.Delivery;

/// <summary>Replaces a small file atomically: write a temporary file, flush it to disk, then rename over the target.</summary>
internal static class AtomicFile
{
    public static void Replace(string path, ReadOnlySpan<byte> bytes)
    {
        string temp = path + ".tmp";
        using (var stream = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            stream.Write(bytes);
            stream.Flush(flushToDisk: true);
        }
        for (int retry = 0; ; retry++)
        {
            try
            {
                File.Move(temp, path, overwrite: true);
                return;
            }
            catch (Exception ex) when (retry < 3 && ex is IOException or UnauthorizedAccessException &&
                (ex.HResult & 0xffff) is 5 or 32 or 33)
            {
                // Windows can briefly refuse the replacement while another process reads the file
                // (access denied, sharing or lock violation). Retry only this local rename, briefly.
                Thread.Sleep(20 << retry);
            }
        }
    }
}
