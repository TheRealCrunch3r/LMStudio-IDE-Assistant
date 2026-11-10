// ---------------------------------------------------------------------------
// SettingsStore.cs – Persistenz der Einstellungen unter %APPDATA%\LMStudioAssistant\config.json
// (bewusst schlicht: JSON-Datei, kein MemoryCache/Framework-Dependency).
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

        /// <summary>Lädt die Einstellungen; bei fehlender/defekter Datei -> Defaults.</summary>
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
                System.Diagnostics.Debug.WriteLine("[LMStudioAssistant] Settings-Load fehlgeschlagen, Defaults: " + ex.Message);
            }
            return new AssistantSettings();
        }

        /// <summary>Speichert die Einstellungen atomar genug für einen lokalen Client (schreiben in Datei).</summary>
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
                System.Diagnostics.Debug.WriteLine("[LMStudioAssistant] Settings-Save fehlgeschlagen: " + ex.Message);
                throw; // UI zeigt den Fehler an (MessageBox), damit der User es merkt.
            }
        }
    }
}
