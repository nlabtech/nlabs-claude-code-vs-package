using System;
using System.IO;
using Newtonsoft.Json;

namespace Nlabs.ClaudeCodeVsPackage.Bridge.Agent;

/// <summary>The panel's global choices - the ones that aren't tied to a single workspace.</summary>
public sealed class PanelPreferences
{
    public string Language { get; set; } = "en";
    public string Accent { get; set; } = "Indigo";
}

/// <summary>
/// Loads and saves the panel's preferences (language, accent) to one JSON file under local app data.
/// Best-effort, like the conversation store: a read that fails just yields the defaults.
/// </summary>
public sealed class PanelPreferencesStore
{
    private readonly string _path;

    public PanelPreferencesStore()
    {
        _path = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "nLabtech", "ClaudeCodePanel", "prefs.json");
    }

    public PanelPreferences Load()
    {
        try
        {
            if (!File.Exists(_path)) return new PanelPreferences();
            return JsonConvert.DeserializeObject<PanelPreferences>(File.ReadAllText(_path)) ?? new PanelPreferences();
        }
        catch
        {
            return new PanelPreferences();
        }
    }

    public void Save(PanelPreferences prefs)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            File.WriteAllText(_path, JsonConvert.SerializeObject(prefs, Formatting.Indented));
        }
        catch
        {
            // a preference that can't be saved is not worth breaking the panel over
        }
    }
}
