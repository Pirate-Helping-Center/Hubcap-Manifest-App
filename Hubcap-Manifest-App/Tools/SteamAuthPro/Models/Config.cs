using HubcapManifestApp.Helpers;
using System;
using System.Collections.Generic;
using System.IO;
using Newtonsoft.Json;

namespace HubcapManifestApp.Tools.SteamAuthPro.Models
{
    public enum TicketDumpMethod
    {
        GetETicket,
        OpenSteamtools
    }

    public class Account
    {
        public string Name { get; set; } = string.Empty;
        public string SteamId { get; set; } = string.Empty;
    }

    public class Config
    {
        private static readonly string ConfigFile = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            AppConstants.AppDataFolderName,
            "steamauthpro_config.json"
        );

        public string ApiUrl { get; set; } = "https://drm.steam.run/api/submit_encrypted_ticket.php";
        public string PhpSessionId { get; set; } = string.Empty;
        public List<Account> Accounts { get; set; } = new();
        public int? ActiveAccount { get; set; }
        public TicketDumpMethod TicketMethod { get; set; } = TicketDumpMethod.GetETicket;

        public string CurrentPhpSessionId => PhpSessionId;

        public static Config Load()
        {
            try
            {
                var directory = Path.GetDirectoryName(ConfigFile);
                if (directory != null && !Directory.Exists(directory))
                {
                    Directory.CreateDirectory(directory);
                }

                if (!File.Exists(ConfigFile))
                    return new Config();

                var json = File.ReadAllText(ConfigFile);
                var config = JsonConvert.DeserializeObject<Config>(json);
                return config ?? new Config();
            }
            catch
            {
                return new Config();
            }
        }

        public void Save()
        {
            try
            {
                var directory = Path.GetDirectoryName(ConfigFile);
                if (directory != null && !Directory.Exists(directory))
                {
                    Directory.CreateDirectory(directory);
                }

                var json = JsonConvert.SerializeObject(this, Formatting.Indented);
                File.WriteAllText(ConfigFile, json);
            }
            catch
            {
                // Ignore save errors
            }
        }

        public void AddAccount(string name, string steamId = "")
        {
            Accounts.Add(new Account { Name = name, SteamId = steamId });
            if (!ActiveAccount.HasValue)
            {
                ActiveAccount = 0;
            }
        }

        public void RemoveAccount(int index)
        {
            if (index >= 0 && index < Accounts.Count)
            {
                Accounts.RemoveAt(index);
                if (ActiveAccount == index)
                {
                    ActiveAccount = Accounts.Count > 0 ? 0 : null;
                }
                else if (ActiveAccount.HasValue && ActiveAccount.Value > index)
                {
                    ActiveAccount--;
                }
            }
        }

        public void SetActiveAccount(int index)
        {
            if (index >= 0 && index < Accounts.Count)
            {
                ActiveAccount = index;
            }
        }
    }
}
