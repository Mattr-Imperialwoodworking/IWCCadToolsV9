using System;
using System.IO;
using System.Text.Json;

namespace IWCCadToolsV9.Helpers
{
    /// <summary>
    /// Per-user JSON settings stored in the Windows Application Data folder.
    /// Settings file: %APPDATA%\IWCCadToolsV9\UserSettings.json
    /// </summary>
    public class IWCUserSettings
    {
        // ---------------------------------------------------------------------------
        // Properties
        // ---------------------------------------------------------------------------

        /// <summary>
        /// Relative path from the user's profile folder to the block library directory.
        /// Example: "Imperial Woodworking Company\05 - Engineering\05.99 - Block Library"
        /// </summary>
        public string BlockLibraryRelativePath { get; set; } =
            @"Imperial Woodworking Company\Imperial Woodworking Company - 05 - Engineering\05.99 - Block Library";

        /// <summary>Default layer name used when inserting blocks.</summary>
        public string DefaultInsertLayer { get; set; } = "0";

        /// <summary>Windows login name of this user (informational).</summary>
        public string UserName { get; set; } = string.Empty;

        /// <summary>Last project number used (informational).</summary>
        public string LastUsedProject { get; set; } = string.Empty;

        // ---------------------------------------------------------------------------
        // Computed helpers
        // ---------------------------------------------------------------------------

        /// <summary>Returns the fully resolved block library path for this user.</summary>
        public string GetBlockLibraryPath()
        {
            var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            var rel = (BlockLibraryRelativePath ?? string.Empty)
                .Replace('/', Path.DirectorySeparatorChar)
                .Replace('\\', Path.DirectorySeparatorChar);
            return Path.Combine(profile, rel);
        }

        // ---------------------------------------------------------------------------
        // Persistence
        // ---------------------------------------------------------------------------

        private static string SettingsPath =>
            Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "IWCCadToolsV9",
                "UserSettings.json");

        private static readonly JsonSerializerOptions _jsonOpts =
            new() { WriteIndented = true };

        /// <summary>
        /// Loads settings from disk, or returns defaults if the file is missing or corrupt.
        /// </summary>
        public static IWCUserSettings Load()
        {
            try
            {
                if (File.Exists(SettingsPath))
                {
                    var json     = File.ReadAllText(SettingsPath);
                    var settings = JsonSerializer.Deserialize<IWCUserSettings>(json);
                    if (settings != null)
                        return settings;
                }
            }
            catch { /* ignore corrupt file, fall through to defaults */ }

            var defaults = new IWCUserSettings();
            defaults.Save(); // create file with defaults
            return defaults;
        }

        /// <summary>Saves current settings to disk.</summary>
        public void Save()
        {
            var dir = Path.GetDirectoryName(SettingsPath)!;
            if (!Directory.Exists(dir))
                Directory.CreateDirectory(dir);

            File.WriteAllText(SettingsPath, JsonSerializer.Serialize(this, _jsonOpts));
        }
    }
}
