using LetheAISharp.Agent;
using LetheAISharp.LLM;
using Microsoft.Extensions.Logging;
using ObsidianToolset.Files;
using System.Reflection;

namespace ObsidianToolset
{

    public sealed class ObsidianLethePlugin : IToolPluginEntry
    {
        public static ObsidianSettings Settings { get; private set; } = new();

        public void Register()
        {
            LLMEngine.Logger?.LogInformation("w(ai)fu Plugin: entry point invoked.");
            // get the plugin directory (the directory where the DLL is located)
            var pluginDir = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location);
            if (pluginDir == null)
            {
                LLMEngine.Logger?.LogError("Failed to determine plugin directory for ObsidianLethePlugin.");
                return;
            }
            // check if settings file exists in the plugin directory
            var settingsPath = Path.Combine(pluginDir, "ObsidianToolset.json");
            if (!File.Exists(settingsPath))
            {
                LLMEngine.Logger?.LogError("ObsidianToolset.json file not found for ObsidianLethePlugin. File created, but you need to edit it to specify your Obsidian vault path.");
                // create file
                var defaultSettings = new ObsidianSettings()
                {
                    VaultPath = string.Empty
                };
                File.WriteAllText(settingsPath, System.Text.Json.JsonSerializer.Serialize(defaultSettings, new System.Text.Json.JsonSerializerOptions() { WriteIndented = true }));
                return;
            }

            // read settings
            var settingsJson = File.ReadAllText(settingsPath);
            try
            {
                var settings = System.Text.Json.JsonSerializer.Deserialize<ObsidianSettings>(settingsJson);
                if (settings != null)
                {
                    Settings = settings;
                }
            }
            catch (Exception ex)
            {
                LLMEngine.Logger?.LogError("Failed to deserialize ObsidianToolset.json for ObsidianLethePlugin. Exception: {mess}", ex.Message);
            }
        }
    }

}
