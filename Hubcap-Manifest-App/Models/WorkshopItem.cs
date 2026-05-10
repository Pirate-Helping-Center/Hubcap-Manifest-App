namespace HubcapManifestApp.Models
{
    public class WorkshopItem
    {
        public string WorkshopId { get; set; } = string.Empty;
        public uint AppId { get; set; }
        public uint DepotId { get; set; }
        public ulong ManifestId { get; set; }
        public string Title { get; set; } = string.Empty;
        public string ManifestFilePath { get; set; } = string.Empty;
        public string DepotKey { get; set; } = string.Empty;
        public string Status { get; set; } = "Pending";
    }
}
