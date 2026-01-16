using System;
using System.IO;
using System.Text.Json;

namespace HeartBeats;

public sealed class HudSettings
{
    // 用像素保存（避免不同缩放导致的 Top/Y 漂移）
    public int Xpx { get; set; } = int.MinValue;
    public int Ypx { get; set; } = int.MinValue;
    public bool Collapsed { get; set; } = false;

    // 0=Off, 1=Minimal(简洁), 2=Ticks(刻度)
    public int GridMode { get; set; } = 1;

    // 曲线显示窗口（秒）
    public int ChartWindowSeconds { get; set; } = 60;

    public static string SettingsPath =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "HeartBeats", "settings.json");

    public static HudSettings Load()
    {
        try
        {
            if (!File.Exists(SettingsPath)) return new HudSettings();
            var json = File.ReadAllText(SettingsPath);
            return JsonSerializer.Deserialize<HudSettings>(json) ?? new HudSettings();
        }
        catch
        {
            return new HudSettings();
        }
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(SettingsPath)!);
            var json = JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(SettingsPath, json);
        }
        catch { }
    }
}
