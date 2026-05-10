using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text.RegularExpressions;
using Microsoft.Win32;
using HubcapManifestApp.Helpers;

namespace HubcapManifestApp.Tools.SteamAuthPro.Models
{
    public class SteamAccount
    {
        public string SteamId { get; set; } = string.Empty;
        public string AccountName { get; set; } = string.Empty;
        public string PersonaName { get; set; } = string.Empty;
        public bool RememberPassword { get; set; }
        public bool MostRecent { get; set; }
    }

    public static class SteamAccountManager
    {
        private static string? GetSteamPath()
        {
            // Try registry first (64-bit)
            try
            {
                using var key = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\WOW6432Node\Valve\Steam");
                if (key != null)
                {
                    var installPath = key.GetValue("InstallPath") as string;
                    if (!string.IsNullOrEmpty(installPath) && Directory.Exists(installPath))
                    {
                        return installPath;
                    }
                }
            }
            catch { }

            // Try registry (32-bit)
            try
            {
                using var key = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\\Valve\\Steam");
                if (key != null)
                {
                    var installPath = key.GetValue("InstallPath") as string;
                    if (!string.IsNullOrEmpty(installPath) && Directory.Exists(installPath))
                    {
                        return installPath;
                    }
                }
            }
            catch { }

            // Fallback to common locations
            var commonPaths = new[]
            {
                @"C:\Program Files (x86)\Steam",
                @"C:\Program Files\Steam"
            };

            foreach (var path in commonPaths)
            {
                if (Directory.Exists(path) && File.Exists(Path.Combine(path, SteamPaths.SteamExe)))
                {
                    return path;
                }
            }

            return null;
        }

        public static Dictionary<string, SteamAccount> GetSteamAccounts()
        {
            var steamPath = GetSteamPath();
            if (string.IsNullOrEmpty(steamPath))
                return new Dictionary<string, SteamAccount>();

            var loginUsersVdf = Path.Combine(steamPath, "config", "loginusers.vdf");
            return ParseVdf(loginUsersVdf);
        }

        public static string? GetCurrentSteamAccount()
        {
            var accounts = GetSteamAccounts();
            foreach (var kvp in accounts)
            {
                if (kvp.Value.MostRecent)
                    return kvp.Key;
            }
            return null;
        }

        public static void SwitchSteamAccount(string targetSteamId)
        {
            var steamPath = GetSteamPath();
            if (string.IsNullOrEmpty(steamPath))
                throw new Exception("Steam installation not found");

            var accounts = GetSteamAccounts();
            if (!accounts.ContainsKey(targetSteamId))
                throw new Exception($"Steam account {targetSteamId} not found");

            var targetUsername = accounts[targetSteamId].AccountName;

            try
            {
                // STEP 1: Kill ALL Steam processes
                var steamProcesses = new[] { "steamwebhelper", "steamservice", "steam" };
                foreach (var processName in steamProcesses)
                {
                    try
                    {
                        var processes = Process.GetProcessesByName(processName);
                        foreach (var process in processes)
                        {
                            try
                            {
                                process.Kill();
                                process.WaitForExit(2000);
                            }
                            finally
                            {
                                process.Dispose();
                            }
                        }
                    }
                    catch { }
                }
                System.Threading.Thread.Sleep(5000); // Wait for all Steam processes to fully close

                // STEP 2: Set Windows Registry AutoLoginUser only
                try
                {
                    using var key = Registry.CurrentUser.OpenSubKey(@"Software\Valve\Steam", true);
                    if (key != null)
                    {
                        key.SetValue("AutoLoginUser", targetUsername, RegistryValueKind.String);
                    }
                    else
                    {
                        throw new Exception("Steam registry key not found");
                    }
                }
                catch (Exception regError)
                {
                    throw new Exception($"Failed to set registry: {regError.Message}");
                }

                // STEP 3: Restart Steam (let it update the VDF itself)
                var steamExe = Path.Combine(steamPath, SteamPaths.SteamExe);
                if (File.Exists(steamExe))
                {
                    Process.Start(new ProcessStartInfo
                    {
                        FileName = steamExe,
                        UseShellExecute = true
                    });
                    System.Threading.Thread.Sleep(10000); // Wait for Steam to fully load and login
                }
            }
            catch (Exception e)
            {
                throw new Exception($"Failed to switch Steam account: {e.Message}");
            }
        }

        private static Dictionary<string, SteamAccount> ParseVdf(string filePath)
        {
            var accounts = new Dictionary<string, SteamAccount>();

            if (!File.Exists(filePath))
                return accounts;

            try
            {
                var content = File.ReadAllText(filePath);

                // Match SteamID64 blocks
                var pattern = @"""(\d{17})""[\s\S]*?\{[\s\S]*?\}";
                var matches = Regex.Matches(content, pattern);

                foreach (Match match in matches)
                {
                    var steamId = match.Groups[1].Value;
                    var block = match.Value;

                    var accountNameMatch = Regex.Match(block, @"""AccountName""\s+""([^""]+)""");
                    var personaNameMatch = Regex.Match(block, @"""PersonaName""\s+""([^""]+)""");
                    var rememberPwMatch = Regex.Match(block, @"""RememberPassword""\s+""([^""]+)""");
                    var mostRecentMatch = Regex.Match(block, @"""MostRecent""\s+""([^""]+)""");

                    if (accountNameMatch.Success)
                    {
                        accounts[steamId] = new SteamAccount
                        {
                            SteamId = steamId,
                            AccountName = accountNameMatch.Groups[1].Value,
                            PersonaName = personaNameMatch.Success ? personaNameMatch.Groups[1].Value : string.Empty,
                            RememberPassword = rememberPwMatch.Success && rememberPwMatch.Groups[1].Value == "1",
                            MostRecent = mostRecentMatch.Success && mostRecentMatch.Groups[1].Value == "1"
                        };
                    }
                }
            }
            catch
            {
                // Return empty dictionary on error
            }

            return accounts;
        }
    }
}
