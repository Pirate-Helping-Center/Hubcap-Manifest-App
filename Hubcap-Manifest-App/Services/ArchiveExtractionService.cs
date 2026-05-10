using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;

namespace HubcapManifestApp.Services
{
    public class ArchiveExtractionService
    {
        public static bool IsValidLuaFilename(string filename)
        {
            if (!filename.ToLower().EndsWith(".lua"))
                return false;

            var namePart = filename.Substring(0, filename.Length - 4);
            return namePart.All(char.IsDigit);
        }

        public (List<string> luaFiles, string? tempDir) ExtractLuaFromArchive(string archivePath)
        {
            var luaFiles = new List<string>();
            var tempDir = Path.Combine(Path.GetTempPath(), $"hubcap_extract_{Guid.NewGuid()}");

            try
            {
                Directory.CreateDirectory(tempDir);

                var archiveLower = archivePath.ToLower();

                if (archiveLower.EndsWith(".zip"))
                {
                    ZipFile.ExtractToDirectory(archivePath, tempDir);
                }
                else if (archiveLower.EndsWith(".rar") || archiveLower.EndsWith(".7z"))
                {
                    // RAR and 7z support would require additional NuGet packages:
                    // SharpCompress or similar libraries
                    throw new NotSupportedException($"Archive format not supported. Please extract manually or use ZIP format.");
                }
                else
                {
                    throw new NotSupportedException($"Unsupported archive format: {Path.GetExtension(archivePath)}");
                }

                // Find all .lua files recursively
                foreach (var file in Directory.GetFiles(tempDir, "*.lua", SearchOption.AllDirectories))
                {
                    var filename = Path.GetFileName(file);
                    if (IsValidLuaFilename(filename))
                    {
                        luaFiles.Add(file);
                    }
                }

                return (luaFiles, tempDir);
            }
            catch (Exception ex)
            {
                // Clean up temp directory on error
                if (Directory.Exists(tempDir))
                {
                    try
                    {
                        Directory.Delete(tempDir, true);
                    }
                    catch
                    {
                        // Ignore cleanup errors - temp directory will be cleaned up by OS eventually
                    }
                }

                throw new Exception($"Error extracting archive: {ex.Message}", ex);
            }
        }

        public void CleanupTempDirectory(string? tempDir)
        {
            if (!string.IsNullOrEmpty(tempDir) && Directory.Exists(tempDir))
            {
                try
                {
                    Directory.Delete(tempDir, true);
                }
                catch
                {
                    // Ignore cleanup errors - temp directory will be cleaned up by OS eventually
                }
            }
        }
    }
}
