using System.Text.Json;

using System.IO;
using System;

namespace Clipper.Services;

public sealed class SettingsStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true
    };

    public int SelectedClipSeconds { get; set; } = 30;

    private string FolderPath => System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "Clipper");

    private string FilePath => System.IO.Path.Combine(FolderPath, "settings.json");

    public void Load()
    {
        try
        {
            if (!System.IO.File.Exists(FilePath))
                return;

            var json = System.IO.File.ReadAllText(FilePath);
            var data = JsonSerializer.Deserialize<SettingsStore>(json, JsonOptions);
            if (data is not null)
                SelectedClipSeconds = data.SelectedClipSeconds;
        }
        catch
        {
            // Fallback to defaults if the file is damaged.
            SelectedClipSeconds = 30;
        }
    }

    public void Save()
    {
        System.IO.Directory.CreateDirectory(FolderPath);
        var json = JsonSerializer.Serialize(this, JsonOptions);
        System.IO.File.WriteAllText(FilePath, json);
    }
}
