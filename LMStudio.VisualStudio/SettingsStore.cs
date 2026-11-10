// ---------------------------------------------------------------------------
// SettingsStore.cs – Persist settings under %APPDATA%\LMStudioAssistant\config.json
// (intentionally simple: JSON file, no MemoryCache / framework dependency).
// ---------------------------------------------------------------------------
using System;
using System.IO;
using System.Text.Json;

namespace LMStudio.VisualStudio
{
    public static class SettingsStore
    {
        private const string FolderName = "LMStudioAssistant";
        private const string FileName = "config.json";

        private static string SettingsPath => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), FolderName, FileName);

        /// <summary>Loads settings; defaults on missing/corrupt file.</summary>
        public static AssistantSettings Load()
        {
            try
            {
                if (File.Exists(SettingsPath))
                {
                    var json = File.ReadAllText(SettingsPath);
                    var loaded = JsonSerializer.Deserialize<AssistantSettings>(json);
                    if (loaded != null) return loaded;
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine("[LMStudioAssistant] Settings load failed, defaults: " + ex.Message);
            }
            return new AssistantSettings();
        }

        /// <summary>Saves settings atomically enough for a local client (writes to file).</summary>
        public static void Save(AssistantSettings settings)
        {
            try
            {
                var dir = Path.GetDirectoryName(SettingsPath);
                Directory.CreateDirectory(dir);
                File.WriteAllText(SettingsPath, JsonSerializer.Serialize(settings, new JsonSerializerOptions { WriteIndented = true }));
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine("[LMStudioAssistant] Settings save failed: " + ex.Message);
                throw; // UI shows the error via MessageBox so the user notices.
            }
        }
    }
}
