using System.Reflection;
using System.IO;
using System.Text;
using System.Text.Json;

namespace RavenMapPanel;

internal static class AssetStore
{
    private static readonly Assembly Assembly = typeof(AssetStore).Assembly;
    private static string Resource(string suffix) => Assembly.GetManifestResourceNames()
        .First(n => n.EndsWith(suffix.Replace('\\', '.'), StringComparison.OrdinalIgnoreCase));

    public static Stream Open(string suffix) => Assembly.GetManifestResourceStream(Resource(suffix))!;

    public static string ReadText(string suffix)
    {
        using var stream = Open(suffix);
        using var reader = new StreamReader(stream, Encoding.UTF8, true);
        return reader.ReadToEnd();
    }

    public static List<MonumentRule> LoadCatalog()
    {
        using var doc = JsonDocument.Parse(ReadText("config.monuments.json"));
        var root = doc.RootElement;
        if (root.ValueKind == JsonValueKind.Object && root.TryGetProperty("monuments", out var nested)) root = nested;
        var result = new List<MonumentRule>();
        foreach (var item in root.EnumerateArray())
        {
            static string S(JsonElement e, string n) => e.TryGetProperty(n, out var p) ? p.GetString() ?? "" : "";
            static List<string> A(JsonElement e, string n) => e.TryGetProperty(n, out var p) && p.ValueKind == JsonValueKind.Array
                ? p.EnumerateArray().Select(x => x.GetString() ?? "").Where(x => x.Length > 0).ToList() : [];
            result.Add(new MonumentRule {
                Id = S(item, "id"), Name = FixText(S(item, "name")), Category = FixText(S(item, "kategori")), Icon = S(item, "icon"),
                Prefabs = A(item, "prefabs"), Aliases = A(item, "aliases"),
                RenderOnMap = !item.TryGetProperty("render_on_map", out var render) || render.GetBoolean(),
                CustomPrefabAvailable = item.TryGetProperty("custom_prefab_available", out var customPrefab) && customPrefab.ValueKind == JsonValueKind.True
            });
        }
        result = result.Where(x => x.Id.Length > 0).ToList();
        foreach (var custom in LoadCustomCatalog())
        {
            custom.IsCustom = true;
            result.RemoveAll(x => x.Id.Equals(custom.Id, StringComparison.OrdinalIgnoreCase));
            result.Add(custom);
        }
        return result;
    }

    private static List<MonumentRule> LoadCustomCatalog()
    {
        try
        {
            if (!File.Exists(SettingsStore.CustomCatalogPath)) return [];
            return JsonSerializer.Deserialize<List<MonumentRule>>(File.ReadAllText(SettingsStore.CustomCatalogPath),
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true }) ?? [];
        }
        catch { return []; }
    }

    public static void SaveCustomRule(MonumentRule rule)
    {
        var rules = LoadCustomCatalog();
        rules.RemoveAll(x => x.Id.Equals(rule.Id, StringComparison.OrdinalIgnoreCase));
        rule.IsCustom = true;
        rules.Add(rule);
        File.WriteAllText(SettingsStore.CustomCatalogPath, JsonSerializer.Serialize(rules.OrderBy(x => x.Name),
            new JsonSerializerOptions { WriteIndented = true }));
    }

    public static (string FileName, int Width, int Height) ImportCustomIcon(string sourcePath, string ruleId)
    {
        if (!File.Exists(sourcePath)) throw new FileNotFoundException("PNG dosyası bulunamadı.", sourcePath);
        if (!Path.GetExtension(sourcePath).Equals(".png", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Monument ikonu PNG biçiminde olmalıdır.");
        using var bitmap = new Bitmap(sourcePath);
        if (bitmap.Width != bitmap.Height) throw new InvalidDataException("PNG kare olmalıdır. Önerilen ölçü 128 × 128 pikseldir.");
        if (bitmap.Width is < 64 or > 512) throw new InvalidDataException("PNG ölçüsü 64 × 64 ile 512 × 512 arasında olmalıdır. Önerilen: 128 × 128.");
        var file = SettingsStore.SafeName(ruleId, "custom_monument") + ".png";
        File.Copy(sourcePath, Path.Combine(SettingsStore.CustomIconsRoot, file), true);
        return (file, bitmap.Width, bitmap.Height);
    }

    public static string? CustomIconPath(string file)
    {
        var path = Path.Combine(SettingsStore.CustomIconsRoot, Path.GetFileName(file));
        return File.Exists(path) ? path : null;
    }

    public static string IconSource(string file) => CustomIconPath(file) ?? $"/Assets/icons/{file}";

    private static string FixText(string value)
    {
        if (!value.Contains('Ã') && !value.Contains('Ä') && !value.Contains('Å')) return value;
        try { return Encoding.UTF8.GetString(Encoding.Latin1.GetBytes(value)); }
        catch { return value; }
    }

    public static Bitmap? LoadIcon(string file)
    {
        if (string.IsNullOrWhiteSpace(file)) return null;
        try
        {
            var custom = CustomIconPath(file);
            if (custom is not null) return new Bitmap(custom);
            using var stream = Open("icons." + file); using var image = Image.FromStream(stream); return new Bitmap(image);
        }
        catch { return null; }
    }


    public static void CopyEmbedded(string suffix, string destination)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        using var input = Open(suffix);
        using var output = File.Create(destination);
        input.CopyTo(output);
    }

    public static void ExtractHarmonyMods(string rustRoot)
    {
        var destination = Path.Combine(rustRoot, "HarmonyMods");
        Directory.CreateDirectory(destination);
        foreach (var name in Assembly.GetManifestResourceNames().Where(n => n.Contains("Assets.HarmonyMods.") && n.EndsWith(".dll")))
        {
            var file = name[(name.IndexOf("Assets.HarmonyMods.", StringComparison.Ordinal) + "Assets.HarmonyMods.".Length)..];
            using var input = Assembly.GetManifestResourceStream(name)!;
            using var output = File.Create(Path.Combine(destination, file));
            input.CopyTo(output);
        }
    }
}
