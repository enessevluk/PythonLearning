using System.Drawing.Drawing2D;
using System.Text.Json;

namespace RavenMapPanel;

internal sealed class OverlayService(List<MonumentRule> catalog)
{
    private sealed record ReportInfo(int WorldSize, int MapResolution, int RenderOffset, int ImageWidth, int ImageHeight, List<MapEntry> Entries);

    public (int Icons, int GodRocks) Render(string rawImage, string outputImage, string reportFolder, int size, int seed)
    {
        var mainPath = Path.Combine(reportFolder, $"RavenMapReport_{size}_{seed}.json");
        var worldPath = Path.Combine(reportFolder, $"RavenWorldObjectReport_{size}_{seed}.json");
        var main = ReadReport(mainPath, size);
        var world = ReadReport(worldPath, size);
        var entries = MergeEntries(main.Entries, world.Entries);

        using var source = new Bitmap(rawImage);
        using var canvas = new Bitmap(source.Width, source.Height, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
        using var graphics = Graphics.FromImage(canvas);
        graphics.DrawImageUnscaled(source, 0, 0);
        graphics.SmoothingMode = SmoothingMode.HighQuality;
        graphics.InterpolationMode = InterpolationMode.HighQualityBicubic;
        graphics.PixelOffsetMode = PixelOffsetMode.HighQuality;

        var resolution = main.MapResolution > 0 ? main.MapResolution : world.MapResolution;
        var offset = main.RenderOffset >= 0 ? main.RenderOffset : world.RenderOffset;
        if (resolution <= 0) { resolution = Math.Min(source.Width, source.Height); offset = 0; }
        var reportWidth = main.ImageWidth > 0 ? main.ImageWidth : source.Width;
        var reportHeight = main.ImageHeight > 0 ? main.ImageHeight : source.Height;
        var iconBase = Math.Clamp((int)(Math.Min(source.Width, source.Height) * .016), 36, 58);
        var drawn = 0;
        foreach (var entry in entries)
        {
            var rule = catalog.FirstOrDefault(x => x.Id.Equals(entry.Category, StringComparison.OrdinalIgnoreCase));
            if (rule is null || !rule.RenderOnMap) continue;
            using var icon = AssetStore.LoadIcon(rule.Icon);
            if (icon is null) continue;

            // Bitmap Y aşağı büyürken Rust dünya +Z ekseni haritada yukarı gider.
            var reportX = offset + ((entry.X + size / 2d) / size) * resolution;
            var reportY = offset + ((size / 2d - entry.Z) / size) * resolution;
            var px = (float)(reportX * source.Width / reportWidth);
            var py = (float)(reportY * source.Height / reportHeight);
            var visible = VisibleBounds(icon);
            var maxSide = Math.Max(visible.Width, visible.Height);
            var categoryScale = IsCompactIcon(entry.Category) ? .52f : 1f;
            var drawWidth = iconBase * categoryScale * visible.Width / (float)Math.Max(1, maxSide);
            var drawHeight = iconBase * categoryScale * visible.Height / (float)Math.Max(1, maxSide);
            graphics.DrawImage(icon, new RectangleF(px - drawWidth / 2, py - drawHeight / 2, drawWidth, drawHeight), visible, GraphicsUnit.Pixel);
            drawn++;
        }
        Directory.CreateDirectory(Path.GetDirectoryName(outputImage)!);
        canvas.Save(outputImage, System.Drawing.Imaging.ImageFormat.Png);
        return (drawn, entries.Count(x => x.Category == "god_rocks"));
    }

    private List<MapEntry> MergeEntries(List<MapEntry> main, List<MapEntry> world)
    {
        var normal = new List<MapEntry>();
        foreach (var e in world.Concat(main))
        {
            var category = ResolveCategory(e);
            if (category.Length == 0 || category == "god_rocks") continue;
            var resolved = e with { Category = category };
            var dedupe = category == "powerlines" ? 12d : 10d;
            if (!normal.Any(x => x.Category == category && Distance(x, resolved) < dedupe)) normal.Add(resolved);
        }

        var rocks = world.Concat(main)
            .Select(x => x with { Category = ResolveCategory(x) })
            .Where(x => x.Category.Equals("god_rocks", StringComparison.OrdinalIgnoreCase))
            .Where(IsExactGodRock).ToList();
        rocks = rocks.OrderByDescending(x => x.Confidence).Aggregate(new List<MapEntry>(), (list, item) =>
        {
            if (!list.Any(x => Distance(x, item) < 60)) list.Add(item);
            return list;
        });
        normal.AddRange(rocks.Select(x => x with { Category = "god_rocks" }));
        return normal;
    }

    private string ResolveCategory(MapEntry entry)
    {
        if (entry.Category.Equals("mining_outpost", StringComparison.OrdinalIgnoreCase)
            && catalog.Any(x => x.Id.Equals("warehouse", StringComparison.OrdinalIgnoreCase))) return "warehouse";
        if (catalog.Any(x => x.Id.Equals(entry.Category, StringComparison.OrdinalIgnoreCase))) return entry.Category.ToLowerInvariant();
        var haystack = (entry.RawName + " " + entry.Name).ToLowerInvariant();
        return catalog.FirstOrDefault(rule => rule.Prefabs.Concat(rule.Aliases).Append(rule.Id)
            .Where(s => !string.IsNullOrWhiteSpace(s)).Any(s => haystack.Contains(s.ToLowerInvariant())))?.Id ?? "";
    }

    private static bool IsExactGodRock(MapEntry entry)
    {
        if (entry.Source.Contains("terrainmeta", StringComparison.OrdinalIgnoreCase)
            || entry.Source.Contains("runtime_scene", StringComparison.OrdinalIgnoreCase)
            || entry.Source.Contains("saved_map_godrock_exact", StringComparison.OrdinalIgnoreCase)
            || entry.Source.Contains("saved_map_godrock_root_a_exact", StringComparison.OrdinalIgnoreCase)) return true;

        var identity = (entry.RawName + " " + entry.Name).Replace('\\', '/').ToLowerInvariant();
        if (identity.Contains("assets/bundled/prefabs/autospawn/decor/v3_rock_formations_large/rock_formation_a.prefab")) return true;
        if (identity.Contains("anvil rock") || identity.Contains("anvil_rock")
            || identity.Contains("arch rock") || identity.Contains("arch_rock")) return false;
        return identity.Contains("godrock") || identity.Contains("god_rock") || identity.Contains("god rock");
    }

    private static double Distance(MapEntry a, MapEntry b) => Math.Sqrt(Math.Pow(a.X - b.X, 2) + Math.Pow(a.Z - b.Z, 2));
    private static bool IsEnvironment(string id) => id is "caves" or "canyons" or "lakes" or "oases";
    private static bool IsCompactIcon(string id) => id is "powerlines" or "power_substations" or "metro_entrances"
        or "caves" or "icebergs" or "god_rocks" or "lakes" or "canyons"
        or "oases" or "swamps" or "water_wells" or "ruins";

    private static Rectangle VisibleBounds(Bitmap bitmap)
    {
        var minX = bitmap.Width; var minY = bitmap.Height; var maxX = -1; var maxY = -1;
        for (var y = 0; y < bitmap.Height; y++) for (var x = 0; x < bitmap.Width; x++)
        {
            if (bitmap.GetPixel(x, y).A <= 20) continue;
            minX = Math.Min(minX, x); minY = Math.Min(minY, y); maxX = Math.Max(maxX, x); maxY = Math.Max(maxY, y);
        }
        return maxX < minX ? new Rectangle(0, 0, bitmap.Width, bitmap.Height) : Rectangle.FromLTRB(minX, minY, maxX + 1, maxY + 1);
    }

    private static ReportInfo ReadReport(string path, int fallbackSize)
    {
        if (!File.Exists(path)) return new(fallbackSize, 0, -1, 0, 0, []);
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            var root = doc.RootElement;
            int I(string n, int d = 0) => root.TryGetProperty(n, out var p) && p.TryGetInt32(out var v) ? v : d;
            var entries = new List<MapEntry>();
            if (root.TryGetProperty("entries", out var array) && array.ValueKind == JsonValueKind.Array)
            foreach (var e in array.EnumerateArray())
            {
                string S(string n) => e.TryGetProperty(n, out var p) ? p.GetString() ?? "" : "";
                double D(string n) => e.TryGetProperty(n, out var p) && p.TryGetDouble(out var v) ? v : 0;
                var category = S("category");
                var source = S("source");
                var raw = S("rawName");
                var confidence = source.Contains("terrainmeta") ? 4 : source.Contains("runtime_scene") ? 3 : source.Contains("saved_map_godrock_exact") ? 2 : 0;
                entries.Add(new(category, S("displayName"), raw, source, D("x"), D("y"), D("z"), confidence));
            }
            return new(I("worldSize", fallbackSize), I("mapResolution"), I("renderOffset", -1), I("imageWidth"), I("imageHeight"), entries);
        }
        catch { return new(fallbackSize, 0, -1, 0, 0, []); }
    }
}
