namespace HubcapManifestApp.Helpers
{
    public static class FormatHelper
    {
        private static readonly string[] SizeUnits = { "B", "KB", "MB", "GB", "TB" };

        /// <summary>
        /// Formats a byte count into a human-readable string (e.g. "1.5 GB").
        /// </summary>
        public static string FormatBytes(long bytes)
        {
            double len = bytes;
            int order = 0;
            while (len >= 1024 && order < SizeUnits.Length - 1)
            {
                order++;
                len /= 1024;
            }
            return $"{len:0.##} {SizeUnits[order]}";
        }
    }
}
