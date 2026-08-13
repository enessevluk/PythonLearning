using System.Text.Json;
using System.Windows.Media.Imaging;

namespace RavenMapPanel;

internal static class MapDataService
{
    public static MapDocument Load(string imagePath, string reports, int size, int seed, IReadOnlyList<MonumentRule> catalog)
    {
        var mainPath = Path.Combine(reports, $"RavenMapReport_{size}_{seed}.json");
        var worldPath = Path.Combine(reports, $"RavenWorldObjectReport_{size}_{seed}.json");
        using var main = JsonDocument.Parse(File.ReadAllText(mainPath));
        var root = main.RootElement;
        int I(string name, int fallback = 0) => root.TryGetProperty(name, out var p) && p.TryGetInt32(out var v) ? v : fallback;
        var bitmap = new BitmapImage(); bitmap.BeginInit(); bitmap.CacheOption = BitmapCacheOption.OnLoad; bitmap.UriSource = new Uri(imagePath); bitmap.EndInit(); bitmap.Freeze();
        var doc = new MapDocument { ImagePath = imagePath, Image = bitmap, ImageWidth = bitmap.PixelWidth, ImageHeight = bitmap.PixelHeight,
            WorldSize = I("worldSize", size), MapResolution = I("mapResolution", bitmap.PixelWidth), RenderOffset = I("renderOffset") };
        var byId = catalog.ToDictionary(x => x.Id, StringComparer.OrdinalIgnoreCase);
        var entries = new List<MapMarker>();
        if (File.Exists(worldPath))
        {
            using var world = JsonDocument.Parse(File.ReadAllText(worldPath));
            if (world.RootElement.TryGetProperty("entries", out var list)) foreach (var e in list.EnumerateArray())
            {
                var category = NormalizeCategory(S(e, "category")); if (!byId.TryGetValue(category, out var rule) || !rule.RenderOnMap) continue;
                var raw = S(e, "rawName"); var source = S(e, "source");
                if (category == "god_rocks" && !ValidGodRock(raw, source)) continue;
                entries.Add(new(category, S(e, "displayName").Length > 0 ? S(e, "displayName") : rule.Name, source, D(e, "x"), D(e, "z"), rule.Icon));
            }
        }
        // Kategorili dünya raporu önceliklidir; renderer raporu yalnızca eksik standart kayıtları tamamlar.
        if (root.TryGetProperty("entries", out var mainEntries)) foreach (var e in mainEntries.EnumerateArray())
        {
            var raw = S(e, "rawName"); var name = S(e, "displayName"); var rule = Resolve(raw + " " + name, catalog); if (rule is null || !rule.RenderOnMap) continue;
            var marker = new MapMarker(rule.Id, name.Length > 0 ? name : rule.Name, "renderer_monument", D(e, "x"), D(e, "z"), rule.Icon);
            if (!entries.Any(x => x.Category == marker.Category && Distance(x, marker) < 8)) entries.Add(marker);
        }
        foreach (var marker in Deduplicate(entries)) doc.Markers.Add(marker);
        return doc;
    }

    private static IEnumerable<MapMarker> Deduplicate(List<MapMarker> entries)
    {
        var result = new List<MapMarker>();
        foreach (var marker in entries.OrderBy(x => SourceRank(x.Source)))
        {
            var radius = marker.Category switch { "powerlines" => 12, "god_rocks" => 60, _ => 8 };
            if (!result.Any(x => x.Category == marker.Category && Distance(x, marker) < radius)) result.Add(marker);
        }
        return result;
    }
    private static int SourceRank(string s) => s.Contains("renderer") ? 0
        : s.Contains("saved_map_powerline_abcd_exact") ? 1
        : s.Contains("runtime_powerline_abcd_exact") ? 2
        : s.Contains("saved_map_powerline_c_exact") ? 3 // backward compatibility with v3.4.3 reports
        : s.Contains("runtime_powerline_c_exact") ? 4   // backward compatibility with v3.4.3 reports
        : s.Contains("saved_map_monument") ? 5
        : s.Contains("saved_map") ? 6 : 7;
    private static string NormalizeCategory(string category) => category.Equals("mining_outpost", StringComparison.OrdinalIgnoreCase) ? "warehouse" : category;
    private static MonumentRule? Resolve(string text, IReadOnlyList<MonumentRule> catalog) { var h = text.ToLowerInvariant(); return catalog.FirstOrDefault(r => r.Prefabs.Concat(r.Aliases).Append(r.Id).Where(x => x.Length > 2).Any(x => h.Contains(x.ToLowerInvariant()))); }
    private static bool ValidGodRock(string raw, string source)
    {
        if (source.Contains("terrainmeta", StringComparison.OrdinalIgnoreCase)
            || source.Contains("runtime_scene", StringComparison.OrdinalIgnoreCase)
            || source.Contains("saved_map_godrock_exact", StringComparison.OrdinalIgnoreCase)
            || source.Contains("saved_map_godrock_root_a_exact", StringComparison.OrdinalIgnoreCase)) return true;

        var identity = (raw ?? "").Replace('\\', '/').ToLowerInvariant();
        if (identity == "assets/bundled/prefabs/autospawn/decor/v3_rock_formations_large/rock_formation_a.prefab") return true;
        if (identity.Contains("anvil rock") || identity.Contains("anvil_rock")
            || identity.Contains("arch rock") || identity.Contains("arch_rock")) return false;
        return identity.Contains("godrock") || identity.Contains("god_rock") || identity.Contains("god rock");
    }
    private static string S(JsonElement e, string n) => e.TryGetProperty(n, out var p) ? p.GetString() ?? "" : "";
    private static double D(JsonElement e, string n) => e.TryGetProperty(n, out var p) && p.TryGetDouble(out var v) ? v : 0;
    private static double Distance(MapMarker a, MapMarker b) => Math.Sqrt(Math.Pow(a.X - b.X, 2) + Math.Pow(a.Z - b.Z, 2));
}
