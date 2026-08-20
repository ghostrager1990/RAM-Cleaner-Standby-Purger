using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;

namespace RamCleaner.Core
{
    public class SettingsData
    {
        public bool StartWithWindows { get; set; } = false;
        public bool StartMinimized { get; set; } = false;
        public bool AutoFlushThresholdEnabled { get; set; } = true;
        public int ThresholdPercent { get; set; } = 85;
        public bool AutoFlushIntervalEnabled { get; set; } = false;
        public int IntervalMinutes { get; set; } = 30;

        // Clean slate: completely empty exclusion list
        public List<string> ExcludedProcesses { get; set; } = new List<string>();
    }

    public static class AppSettings
    {
        private static readonly string SettingsFolder = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "RamCleaner");
        private static readonly string SettingsFilePath = Path.Combine(SettingsFolder, "settings.json");

        public static SettingsData Load()
        {
            try
            {
                if (File.Exists(SettingsFilePath))
                {
                    string json = File.ReadAllText(SettingsFilePath);
                    return JsonSerializer.Deserialize<SettingsData>(json) ?? new SettingsData();
                }
            }
            catch
            {
                // Fallback to fresh defaults if file is corrupt
            }

            return new SettingsData();
        }

        public static void Save(SettingsData settings)
        {
            try
            {
                Directory.CreateDirectory(SettingsFolder);
                string json = JsonSerializer.Serialize(settings, new JsonSerializerOptions { WriteIndented = true });
                File.WriteAllText(SettingsFilePath, json);
            }
            catch
            {
                // Ignore transient write errors
            }
        }
    }
}