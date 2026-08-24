using System;
using System.IO;
using System.Text.Json;

namespace FileTray.Services;

public sealed class SettingsService
{
    private string FilePath => Path.Combine(DirectoryPath, "settings.json");

    public string DirectoryPath { get; }

    public string Alias { get; set; } = Environment.MachineName;
    public string Fingerprint { get; set; } = Guid.NewGuid().ToString("N");
    public int Port { get; set; } = 53317;

    public SettingsService(string directoryPath)
    {
        DirectoryPath = directoryPath;
        try
        {
            Directory.CreateDirectory(directoryPath);
            if (File.Exists(FilePath))
            {
                var json = File.ReadAllText(FilePath);
                var settings = JsonSerializer.Deserialize<AppSettings>(json, Http.Json);
                if (settings != null)
                {
                    if (!string.IsNullOrWhiteSpace(settings.Alias)) Alias = settings.Alias;
                    if (!string.IsNullOrWhiteSpace(settings.Fingerprint)) Fingerprint = settings.Fingerprint;
                    if (settings.Port is > 0 and < 65536) Port = settings.Port;
                }
            }
        }
        catch (Exception ex)
        {
            Log.Warn($"读取设置失败: {ex.Message}");
        }
    }

    public void Save()
    {
        try
        {
            var settings = new AppSettings { Alias = Alias, Fingerprint = Fingerprint, Port = Port };
            File.WriteAllText(FilePath, JsonSerializer.Serialize(settings, Http.Json));
        }
        catch (Exception ex)
        {
            Log.Warn($"保存设置失败: {ex.Message}");
        }
    }

    private sealed class AppSettings
    {
        public string? Alias { get; set; }
        public string? Fingerprint { get; set; }
        public int Port { get; set; }
    }
}
