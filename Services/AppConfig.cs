using System;
using System.IO;
using Microsoft.Win32;
using Newtonsoft.Json;

namespace ProfileSwitchChrome.Services
{
    public class AppConfig
    {
        private static readonly string ConfigDir;
        private static readonly string ConfigFile;

        static AppConfig()
        {
            string newDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "AntigravityHub");
            string oldDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "ProfileSwitchChrome");

            try
            {
                if (Directory.Exists(oldDir) && !Directory.Exists(newDir))
                {
                    Directory.CreateDirectory(newDir);
                    foreach (var file in Directory.GetFiles(oldDir))
                    {
                        string dest = Path.Combine(newDir, Path.GetFileName(file));
                        if (!File.Exists(dest)) File.Copy(file, dest, true);
                    }
                    string oldAvatars = Path.Combine(oldDir, "avatars");
                    string newAvatars = Path.Combine(newDir, "avatars");
                    if (Directory.Exists(oldAvatars) && !Directory.Exists(newAvatars))
                    {
                        Directory.CreateDirectory(newAvatars);
                        foreach (var f in Directory.GetFiles(oldAvatars))
                        {
                            File.Copy(f, Path.Combine(newAvatars, Path.GetFileName(f)), true);
                        }
                    }
                }
            }
            catch { }

            ConfigDir = newDir;
            ConfigFile = Path.Combine(ConfigDir, "config.json");
        }

        public string DefaultProfileId { get; set; } = string.Empty;
        public bool OpenImmediatelyOnSet { get; set; } = false;
        public bool AutoProtectEnabled { get; set; } = true;
        public bool UpdateShortcutsOnSet { get; set; } = true;
        public bool UpdateRegistryOnSet { get; set; } = true;
        public bool StartWithWindows { get; set; } = false;
        public bool MinimizeToTray { get; set; } = true;
        public string ThemeMode { get; set; } = "dark";
        public string Language { get; set; } = "vi";
        public string RecommendationRule { get; set; } = "pro_first";
        public int BatchMeasureConcurrency { get; set; } = 5;
        public bool AutoRefreshAllQuotaEnabled { get; set; } = false;
        public int AutoRefreshAllQuotaIntervalMinutes { get; set; } = 15;
        public int AutoRefreshAllQuotaIntervalSeconds { get; set; } = 0;
        public bool HideAllNames { get; set; } = false;
        public bool HideAllEmails { get; set; } = false;
        public bool HideChromeNames { get; set; } = false;
        public bool HideChromeEmails { get; set; } = false;
        public bool SilentSyncOAuth { get; set; } = true;
        public bool AutoClickSignIn { get; set; } = false;
        public string AntigravitySwitchTarget { get; set; } = "ide"; // "ide", "cli", "antigravity"

        [JsonIgnore]
        public int TotalAutoRefreshIntervalSeconds
        {
            get
            {
                if (AutoRefreshAllQuotaIntervalSeconds > 0)
                    return AutoRefreshAllQuotaIntervalSeconds;
                if (AutoRefreshAllQuotaIntervalMinutes > 0)
                    return AutoRefreshAllQuotaIntervalMinutes * 60;
                return 900;
            }
        }

        public string FormatIntervalDisplay()
        {
            int totalSec = TotalAutoRefreshIntervalSeconds;
            if (totalSec < 60)
            {
                return $"{totalSec}s";
            }
            if (totalSec % 3600 == 0)
            {
                return $"{totalSec / 3600}h";
            }
            if (totalSec % 60 == 0)
            {
                return $"{totalSec / 60}m";
            }
            int m = totalSec / 60;
            int s = totalSec % 60;
            return $"{m}m{s}s";
        }

        public string FormatIntervalDetailed()
        {
            int totalSec = TotalAutoRefreshIntervalSeconds;
            bool isEn = LocalizationManager.IsEnglish;
            if (totalSec < 60)
            {
                return isEn ? $"{totalSec}s" : $"{totalSec} giây";
            }
            if (totalSec % 3600 == 0)
            {
                int h = totalSec / 3600;
                return isEn ? (h == 1 ? "1 hour" : $"{h} hours") : $"{h} giờ";
            }
            if (totalSec % 60 == 0)
            {
                int m = totalSec / 60;
                return isEn ? (m == 1 ? "1 minute" : $"{m} minutes") : $"{m} phút";
            }
            int min = totalSec / 60;
            int sec = totalSec % 60;
            return isEn ? $"{min}m {sec}s" : $"{min} phút {sec} giây";
        }

        public static AppConfig Load()
        {
            try
            {
                if (File.Exists(ConfigFile))
                {
                    string json = File.ReadAllText(ConfigFile);
                    var config = JsonConvert.DeserializeObject<AppConfig>(json);
                    if (config != null)
                    {
                        return config;
                    }
                }
            }
            catch
            {
                // Fallback to default
            }

            return new AppConfig();
        }

        public void Save()
        {
            try
            {
                if (!Directory.Exists(ConfigDir))
                {
                    Directory.CreateDirectory(ConfigDir);
                }

                string json = JsonConvert.SerializeObject(this, Formatting.Indented);
                File.WriteAllText(ConfigFile, json);

                SetStartupRegistry(StartWithWindows);
            }
            catch
            {
                // Ignore error during config saving
            }
        }

        public static void SetStartupRegistry(bool enable)
        {
            try
            {
                using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run", true);
                if (key != null)
                {
                    string appPath = Environment.ProcessPath ?? "";
                    if (enable && !string.IsNullOrEmpty(appPath))
                    {
                        key.SetValue("AntigravityHub", $"\"{appPath}\" --tray");
                        key.DeleteValue("ProfileSwitchChrome", false);
                    }
                    else
                    {
                        key.DeleteValue("AntigravityHub", false);
                        key.DeleteValue("ProfileSwitchChrome", false);
                    }
                }
            }
            catch
            {
                // Silently ignore registry access issue
            }
        }
    }
}
