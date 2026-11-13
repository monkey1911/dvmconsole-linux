// SPDX-License-Identifier: AGPL-3.0-only
/**
* dvmcli - headless Linux proof of concept for the DVM Desktop Dispatch Console.
*
* Remembered TUI settings, kept per system in a small JSON file
* (~/.config/dvmcli/settings.json, or $XDG_CONFIG_HOME/dvmcli/settings.json).
* Holds no passwords or key material.
*/

using System.Text.Json;
using System.Text.Json.Serialization;

namespace dvmcli
{
    /// <summary>
    /// Settings for one channel card.
    /// </summary>
    public sealed class ChannelSettings
    {
        public bool Listening { get; set; }
        public bool Muted { get; set; }
        public double Gain { get; set; } = 1.0;
    }

    /// <summary>
    /// Settings for one system.
    /// </summary>
    public sealed class SystemSettings
    {
        /// <summary>Per channel, keyed by "zone/name/tg".</summary>
        public Dictionary<string, ChannelSettings> Channels { get; set; } = new Dictionary<string, ChannelSettings>();
        public string Focused { get; set; }
        public bool GlobalMute { get; set; }
        public string PageA { get; set; } = string.Empty;
        public string PageB { get; set; } = string.Empty;
        public string PageRid { get; set; } = string.Empty;
    }

    /// <summary>
    /// All remembered settings.
    /// </summary>
    public sealed class AppSettings
    {
        public bool TalkPermitTone { get; set; } = true;
        public Dictionary<string, SystemSettings> Systems { get; set; } = new Dictionary<string, SystemSettings>(StringComparer.OrdinalIgnoreCase);

        private static readonly JsonSerializerOptions Json = new JsonSerializerOptions
        {
            WriteIndented = true,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
        };

        /// <summary>The default settings file.</summary>
        public static string DefaultPath
        {
            get
            {
                string config = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME");
                if (string.IsNullOrWhiteSpace(config))
                    config = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".config");
                return Path.Combine(config, "dvmcli", "settings.json");
            }
        }

        /// <summary>
        /// Loads settings; a missing or unreadable file gives defaults (and <paramref name="error"/> says why, if unreadable).
        /// </summary>
        public static AppSettings Load(string path, out string error)
        {
            error = null;
            try
            {
                if (path == null || !File.Exists(path))
                    return new AppSettings();
                AppSettings s = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(path), Json) ?? new AppSettings();
                s.Systems = new Dictionary<string, SystemSettings>(s.Systems ?? new(), StringComparer.OrdinalIgnoreCase);
                return s;
            }
            catch (Exception ex)
            {
                error = $"settings file {path} unreadable, using defaults ({ex.Message})";
                return new AppSettings();
            }
        }

        /// <summary>
        /// Saves settings (written to a temporary file, then renamed, so a crash can't leave half a file).
        /// </summary>
        public void Save(string path)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path)));
            string tmp = path + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(this, Json));
            File.Move(tmp, path, overwrite: true);
        }

        /// <summary>The settings for a system (created if new).</summary>
        public SystemSettings For(string systemName)
        {
            if (!Systems.TryGetValue(systemName ?? string.Empty, out SystemSettings s))
                Systems[systemName ?? string.Empty] = s = new SystemSettings();
            return s;
        }
    } // public sealed class AppSettings
}
