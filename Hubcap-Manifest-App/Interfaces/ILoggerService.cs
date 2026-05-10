namespace HubcapManifestApp.Interfaces
{
    public interface ILoggerService
    {
        void Debug(string message);
        void Info(string message);
        void Warning(string message);
        void Error(string message);
    }
}
