namespace HubcapManifestApp.Interfaces
{
    public interface ISteamService
    {
        string? GetSteamPath();
        string? GetStPluginPath();
        bool EnsureStPluginDirectory();
    }
}
