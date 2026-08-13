using System.Text.Json;

namespace RavenMapPanel;

internal static class SettingsStore
{
    public static string AppRoot => Path.GetFullPath(AppContext.BaseDirectory);
    public static string ConfigRoot => Ensure(Path.Combine(AppRoot, "Config"));
    public static string DefaultMapsRoot => Ensure(Path.Combine(AppRoot, "Maps"));
    public static string CustomIconsRoot => Ensure(Path.Combine(ConfigRoot, "Icons"));
    public static string MonumentGalleryRoot => Ensure(Path.Combine(ConfigRoot, "MonumentGallery"));
    public static string CustomCatalogPath => Path.Combine(ConfigRoot, "custom_monuments.json");
    public static string GenerationHistoryPath => Path.Combine(ConfigRoot, "generation_history.json");
    private static string FilePath => Path.Combine(ConfigRoot, "settings.json");
    private static string LegacyFilePath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "RavenMapPanel", "settings.json");
    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true, PropertyNameCaseInsensitive = true };

    public static AppSettings Load()
    {
        try
        {
            var source = File.Exists(FilePath) ? FilePath : LegacyFilePath;
            var settings = File.Exists(source) ? JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(source), Options) ?? new() : new();
            MigrateProjectPath(settings);
            if (!File.Exists(FilePath) && File.Exists(source)) Save(settings);
            return settings;
        }
        catch { return new(); }
    }

    public static void Save(AppSettings settings)
    {
        Directory.CreateDirectory(ConfigRoot);
        File.WriteAllText(FilePath, JsonSerializer.Serialize(settings, Options));
    }

    public static string OutputRoot(AppSettings settings)
    {
        var selected = settings.MapsOutputPath?.Trim();
        return Ensure(string.IsNullOrWhiteSpace(selected) ? DefaultMapsRoot : Path.GetFullPath(selected));
    }

    public static string DefaultProjectPath(string name) => Path.Combine(ConfigRoot, SafeName(name, "RavenHarita") + ".ravenmap");
    public static string SafeName(string? value, string fallback)
    {
        var safe = string.Concat((value ?? "").Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c)).Trim().TrimEnd('.');
        return string.IsNullOrWhiteSpace(safe) ? fallback : safe;
    }
    private static void MigrateProjectPath(AppSettings settings)
    {
        if (string.IsNullOrWhiteSpace(settings.CurrentProjectPath)) return;
        var oldPath = Path.GetFullPath(settings.CurrentProjectPath);
        var target = Path.Combine(ConfigRoot, Path.GetFileName(oldPath));
        if (File.Exists(oldPath) && string.Equals(Path.GetDirectoryName(oldPath)?.TrimEnd('\\'), AppRoot.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase))
        {
            if (!File.Exists(target)) File.Copy(oldPath, target);
            settings.CurrentProjectPath = target;
        }
        else if (!File.Exists(oldPath) && File.Exists(target)) settings.CurrentProjectPath = target;
        settings.RecentProjects.RemoveAll(x => x.Equals(oldPath, StringComparison.OrdinalIgnoreCase));
        if (!settings.RecentProjects.Contains(settings.CurrentProjectPath, StringComparer.OrdinalIgnoreCase)) settings.RecentProjects.Insert(0, settings.CurrentProjectPath);
    }
    private static string Ensure(string path) { Directory.CreateDirectory(path); return path; }
}

internal static class GenerationHistoryStore
{
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true
    };

    public static List<GenerationHistoryEntry> Load()
    {
        try
        {
            if (!File.Exists(SettingsStore.GenerationHistoryPath))
                return [];
            return JsonSerializer.Deserialize<List<GenerationHistoryEntry>>(
                File.ReadAllText(SettingsStore.GenerationHistoryPath), Options) ?? [];
        }
        catch
        {
            return [];
        }
    }

    public static void Add(GenerationHistoryEntry entry)
    {
        var items = Load();
        items.RemoveAll(x => string.Equals(x.Id, entry.Id, StringComparison.OrdinalIgnoreCase));
        items.Insert(0, entry);
        if (items.Count > 60)
            items.RemoveRange(60, items.Count - 60);
        Save(items);
    }

    public static void Save(IEnumerable<GenerationHistoryEntry> items)
    {
        Directory.CreateDirectory(SettingsStore.ConfigRoot);
        File.WriteAllText(SettingsStore.GenerationHistoryPath,
            JsonSerializer.Serialize(items, Options));
    }
}
