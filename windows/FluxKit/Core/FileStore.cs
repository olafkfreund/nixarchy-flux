namespace FluxKit;

public static class FileStore
{
    // Writes the file through a temporary file and a rename, so that a crash
    // leaves the old file or the new file. A secret file can only be read by
    // the user. On Windows, the folder in %LOCALAPPDATA% already has that rule.
    public static void WriteAtomic(string path, byte[] data, bool secret = false)
    {
        var directory = Path.GetDirectoryName(Path.GetFullPath(path))!;
        Directory.CreateDirectory(directory);
        var temp = Path.Combine(directory, "." + Path.GetFileName(path) + "." + Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            using (var f = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                if (secret && !OperatingSystem.IsWindows())
                {
                    File.SetUnixFileMode(temp, UnixFileMode.UserRead | UnixFileMode.UserWrite);
                }
                f.Write(data);
                f.Flush(true);
            }
            File.Move(temp, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temp))
            {
                File.Delete(temp);
            }
        }
    }
}
