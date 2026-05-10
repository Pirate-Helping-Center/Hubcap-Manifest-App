using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net.Http;
using System.Reflection;
using System.Text;
using System.Threading.Tasks;

namespace HubcapManifestApp.Services.GBE
{
    public class GoldbergLogic
    {
        private readonly int _appId;
        private readonly string _outputPath;
        private readonly string _apiKey;
        private readonly Action<string, bool> _log;
        private static readonly HttpClient HttpClient = new HttpClient()
        {
            Timeout = TimeSpan.FromSeconds(30)
        };

        public GoldbergLogic(int appId, string outputPath, string apiKey, Action<string, bool> logAction)
        {
            _appId = appId;
            _outputPath = outputPath;
            _apiKey = apiKey;
            _log = logAction;
        }

        public async Task<bool> GenerateAsync()
        {
            string tempDir = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
            string stagingRoot = Path.Combine(tempDir, "staging");
            string settingsPath = Path.Combine(stagingRoot, "steam_settings");

            try
            {
                Directory.CreateDirectory(settingsPath);

                var ticketResult = await GenerateTicketAsync(settingsPath);
                if (!ticketResult.Success)
                {
                    _log("Aborting Goldberg setup due to ticket generation failure.", true);
                    return false;
                }

                _log("\n--- Generating Config Files ---", false);

                WriteAppIdFile(settingsPath);
                await FetchAndWriteAchievementsAsync(settingsPath, _apiKey);
                await FetchAndWriteDepotsAsync(settingsPath);
                await FetchAndWriteDlcsAsync(settingsPath);
                await FetchAndWriteLanguagesAsync(settingsPath);
                WriteControlConfig(settingsPath);
                WriteOverlayConfig(settingsPath);
                WriteMainConfig(settingsPath);

                _log("\n--- Copying Dependencies ---", false);
                ExtractAndCopyDependencies(stagingRoot, settingsPath);

                _log("\n--- Creating Archive ---", false);
                CreateZipArchive(stagingRoot);

                return true;
            }
            catch (Exception ex)
            {
                _log($"\nAn unexpected error occurred: {ex.Message}", true);
                return false;
            }
            finally
            {
                if (Directory.Exists(tempDir))
                {
                    Directory.Delete(tempDir, true);
                }
            }
        }

        private async Task<(bool Success, string? Ticket, ulong SteamId)> GenerateTicketAsync(string settingsPath)
        {
            string tempLibDir = Path.Combine(Path.GetTempPath(), "GBE_Temp_" + Path.GetRandomFileName());
            Directory.CreateDirectory(tempLibDir);

            try
            {
                // Extract DLLs from embedded resources to temp directory
                ExtractEmbeddedResource("HubcapManifestApp.lib.gbe.steam_api64.dll", Path.Combine(tempLibDir, "steam_api64.dll"));
                ExtractEmbeddedResource("HubcapManifestApp.lib.gbe.dependencies.steamclient64.dll", Path.Combine(tempLibDir, "steamclient64.dll"));

                if (!SteamApi.SetDllDirectory(tempLibDir))
                {
                    _log("Failed to set DLL search directory.", true);
                    return (false, null, 0);
                }

                Environment.SetEnvironmentVariable("SteamAppId", _appId.ToString());
                Environment.SetEnvironmentVariable("SteamGameId", _appId.ToString());

                if (SteamApi.SteamAPI_InitFlat(IntPtr.Zero) == 0)
                {
                    IntPtr user = SteamApi.SteamAPI_SteamUser_v023();
                    if (user != IntPtr.Zero)
                    {
                        _log("Requesting ticket from Steam...", false);
                        SteamApi.SteamAPI_ISteamUser_RequestEncryptedAppTicket(user, IntPtr.Zero, 0);
                        await Task.Delay(1500);

                        byte[] ticketBuffer = new byte[2048];
                        if (SteamApi.SteamAPI_ISteamUser_GetEncryptedAppTicket(user, ticketBuffer, ticketBuffer.Length, out uint ticketLen))
                        {
                            byte[] actualTicket = new byte[ticketLen];
                            Array.Copy(ticketBuffer, actualTicket, ticketLen);
                            string ticketB64 = Convert.ToBase64String(actualTicket);
                            ulong steamId = SteamApi.SteamAPI_ISteamUser_GetSteamID(user);

                            _log("✓ Ticket generated successfully!", false);
                            CreateUserConfig(settingsPath, steamId, ticketB64);
                            return (true, ticketB64, steamId);
                        }
                        else
                        {
                            _log("Failed to get encrypted app ticket.", true);
                            _log("  This usually means the logged-in Steam account does not own the game.", true);
                        }
                    } else { _log("Failed to get Steam user interface.", true); }
                } else { _log("Steam API initialization failed.", true); _log("  Make sure Steam is running and you are logged in.", true); }
            }
            catch (Exception ex)
            {
                _log($"An unexpected error occurred: {ex.Message}", true);
            }
            finally
            {
                SteamApi.SetDllDirectory(null);

                // Clean up temp directory
                try
                {
                    if (Directory.Exists(tempLibDir))
                    {
                        Directory.Delete(tempLibDir, true);
                    }
                }
                catch
                {
                    // Ignore cleanup errors - temp directory will be cleaned up by OS eventually
                }
            }
            return (false, null, 0);
        }

        private void CreateUserConfig(string settingsPath, ulong steamId, string ticket)
        {
            var configPath = Path.Combine(settingsPath, "configs.user.ini");
            var content = new StringBuilder();
            content.AppendLine("[user::general]");
            content.AppendLine("account_name=Player");
            content.AppendLine($"account_steamid={steamId}");
            content.AppendLine($"ticket={ticket}");
            content.AppendLine("language=english");
            File.WriteAllText(configPath, content.ToString());
            _log("✓ Created configs.user.ini", false);
        }

        private void WriteAppIdFile(string settingsPath)
        {
            File.WriteAllText(Path.Combine(settingsPath, "steam_appid.txt"), _appId.ToString());
            _log("✓ Created steam_appid.txt", false);
        }

        private async Task FetchAndWriteAchievementsAsync(string settingsPath, string apiKey)
        {
            var imagesDir = Path.Combine(settingsPath, "image");
            Directory.CreateDirectory(imagesDir);
            var achievementsJsonPath = Path.Combine(settingsPath, "achievements.json");

            string url = $"https://api.steampowered.com/ISteamUserStats/GetSchemaForGame/v2/?key={apiKey}&appid={_appId}&l=english";
            _log("Fetching achievements...", false);
            try
            {
                var response = await HttpClient.GetStringAsync(url);
                var data = JObject.Parse(response);
                var achievements = data?["game"]?["availableGameStats"]?["achievements"];

                if (achievements == null || !achievements.HasValues)
                {
                    _log("No achievements found for this App ID.", false);
                    return;
                }

                File.WriteAllText(achievementsJsonPath, achievements.ToString(Formatting.Indented));
                _log($"✓ Saved {achievements.Count()} achievements", false);

                foreach (var ach in achievements)
                {
                    foreach (var iconKey in new[] { "icon", "icongray" })
                    {
                        var iconUrl = (string?)ach[iconKey];
                        if (!string.IsNullOrEmpty(iconUrl))
                        {
                            var fileName = Path.GetFileName(iconUrl);
                            var savePath = Path.Combine(imagesDir, fileName);
                            try
                            {
                                var imgBytes = await HttpClient.GetByteArrayAsync(iconUrl);
                                File.WriteAllBytes(savePath, imgBytes);
                            }
                            catch (Exception ex)
                            {
                                _log($"Failed to download {iconUrl}: {ex.Message}", true);
                            }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                _log($"Could not fetch achievements: {ex.Message}", true);
            }
        }

        private async Task FetchAndWriteDepotsAsync(string settingsPath)
        {
            string url = $"{SteamCmdApiService.BaseUrl}/{_appId}";
            _log("Fetching depots...", false);
            try
            {
                var response = await HttpClient.GetStringAsync(url);
                var data = JObject.Parse(response);
                var depots = data?["data"]?[_appId.ToString()]?["depots"] as JObject;
                if(depots == null)
                {
                    _log("No depot information found for this App ID.", false);
                    return;
                }

                var depotIds = depots.Properties().Select(p => p.Name).Where(name => int.TryParse(name, out _));
                File.WriteAllLines(Path.Combine(settingsPath, "depots.txt"), depotIds);
                _log($"✓ Fetched {depotIds.Count()} depots", false);
            }
            catch(Exception ex)
            {
                _log($"Could not fetch depots: {ex.Message}", true);
            }
        }

        private async Task FetchAndWriteDlcsAsync(string settingsPath)
        {
            string url = $"{SteamCmdApiService.BaseUrl}/{_appId}";
             _log("Fetching DLCs...", false);
            var content = new StringBuilder();
            content.AppendLine("[app::dlcs]");
            content.AppendLine("unlock_all = 0");

            try
            {
                var response = await HttpClient.GetStringAsync(url);
                var data = JObject.Parse(response);
                var dlcString = (string?)data?["data"]?[_appId.ToString()]?["extended"]?["listofdlc"];

                if (!string.IsNullOrEmpty(dlcString))
                {
                    var dlcIds = dlcString.Split(',').Select(id => id.Trim()).Where(id => int.TryParse(id, out _));
                    foreach (var id in dlcIds)
                    {
                        content.AppendLine($"{id} = DLC");
                    }
                    _log($"✓ Fetched {dlcIds.Count()} DLCs", false);
                }
                else
                {
                    _log("No DLCs found, wrote default config.", false);
                }
            }
            catch (Exception ex)
            {
                 _log($"Could not fetch DLCs: {ex.Message}", true);
            }
            finally
            {
                File.WriteAllText(Path.Combine(settingsPath, "configs.app.ini"), content.ToString());
            }
        }

        private async Task FetchAndWriteLanguagesAsync(string settingsPath)
        {
            string url = $"{SteamCmdApiService.BaseUrl}/{_appId}";
            _log("Fetching supported languages...", false);

            try
            {
                 var response = await HttpClient.GetStringAsync(url);
                 var data = JObject.Parse(response);
                 var languagesString = (string?)data?["data"]?[_appId.ToString()]?["depots"]?["baselanguages"];
                 if (!string.IsNullOrEmpty(languagesString))
                 {
                     var languages = languagesString.Split(',');
                     File.WriteAllLines(Path.Combine(settingsPath, "supported_languages.txt"), languages);
                     _log($"✓ Fetched {languages.Length} languages", false);
                     return;
                 }
            }
            catch (Exception ex)
            {
                 _log($"Could not fetch languages: {ex.Message}. Using default list.", true);
            }

            string defaultLanguages = "english\nfrench\nitalian\ngerman\nspanish\narabic\njapanese\nkoreana\npolish\nbrazilian\nrussian\nschinese\nlatam\ntchinese";
            File.WriteAllText(Path.Combine(settingsPath, "supported_languages.txt"), defaultLanguages);
            _log("✓ Created languages file with default list", false);
        }

        private void WriteControlConfig(string settingsPath)
        {
            var controlDir = Path.Combine(settingsPath, "controller");
            Directory.CreateDirectory(controlDir);
            string mappings = "AxisL=LJOY=joystick_move\nAxisR=RJOY=joystick_move\nAnalogL=LTRIGGER=trigger\nAnalogR=RTRIGGER=trigger\nLUp=DUP\nLDown=DDOWN\nLLeft=DLEFT\nLRight=DRIGHT\nRUp=Y\nRDown=A\nRLeft=X\nRRight=B\nCLeft=BACK\nCRight=START\nLStickPush=LSTICK\nRStickPush=RSTICK\nLTrigTop=LBUMPER\nRTrigTop=RBUMPER";
            File.WriteAllText(Path.Combine(controlDir, "controls.txt"), mappings);
            _log("✓ Created controls.txt", false);
        }

        private void WriteOverlayConfig(string settingsPath)
        {
            string content = "[overlay::general]\nenable_experimental_overlay = 1\n";
            File.WriteAllText(Path.Combine(settingsPath, "configs.overlay.ini"), content);
            _log("✓ Created configs.overlay.ini", false);
        }

        private void WriteMainConfig(string settingsPath)
        {
            string content = "[main::connectivity]\ndisable_lan_only=1\n";
            File.WriteAllText(Path.Combine(settingsPath, "configs.main.ini"), content);
            _log("✓ Created configs.main.ini", false);
        }

        private void ExtractAndCopyDependencies(string stagingRoot, string settingsPath)
        {
            // Extract DLL dependencies from embedded resources
            ExtractEmbeddedResource("HubcapManifestApp.Resources.GBE.dependencies.steam_api64.dll", Path.Combine(stagingRoot, "steam_api64.dll"));
            _log("✓ Extracted steam_api64.dll", false);

            ExtractEmbeddedResource("HubcapManifestApp.Resources.GBE.dependencies.steamclient64.dll", Path.Combine(stagingRoot, "steamclient64.dll"));
            _log("✓ Extracted steamclient64.dll", false);

            // Extract sound files from embedded resources
            try
            {
                string destSoundsDir = Path.Combine(settingsPath, "sounds");
                Directory.CreateDirectory(destSoundsDir);

                var assembly = Assembly.GetExecutingAssembly();
                string soundResourcePrefix = "HubcapManifestApp.Resources.GBE.sounds.";

                var soundResourceNames = assembly.GetManifestResourceNames()
                                                .Where(r => r.StartsWith(soundResourcePrefix) && r.EndsWith(".wav"));

                foreach (var resourceName in soundResourceNames)
                {
                    string fileName = resourceName.Substring(soundResourcePrefix.Length);
                    string destFilePath = Path.Combine(destSoundsDir, fileName);
                    ExtractEmbeddedResource(resourceName, destFilePath);
                }

                _log($"✓ Extracted {soundResourceNames.Count()} sound files", false);
            }
            catch (Exception ex)
            {
                _log($"Could not extract sound files: {ex.Message}", true);
            }
        }

        private static void ExtractEmbeddedResource(string resourceName, string outputPath)
        {
            using (var resourceStream = Assembly.GetExecutingAssembly().GetManifestResourceStream(resourceName))
            {
                if (resourceStream == null)
                    throw new FileNotFoundException($"Embedded resource '{resourceName}' not found.");

                using (var fileStream = new FileStream(outputPath, FileMode.Create, FileAccess.Write))
                {
                    resourceStream.CopyTo(fileStream);
                }
            }
        }

        private void CreateZipArchive(string sourceDir)
        {
            try
            {
                if (File.Exists(_outputPath))
                {
                    File.Delete(_outputPath);
                }
                ZipFile.CreateFromDirectory(sourceDir, _outputPath);
                _log($"✓ Successfully created archive: {_outputPath}", false);
            }
            catch (Exception ex)
            {
                _log($"Failed to create ZIP archive: {ex.Message}", true);
            }
        }

    }
}
