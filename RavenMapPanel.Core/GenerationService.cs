using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace RavenMapPanel;

internal sealed class GenerationService(List<MonumentRule> catalog)
{
    public event Action<string>? Log;

    public async Task<GenerationResult> GenerateAsync(AppSettings settings, CancellationToken cancellation)
    {
        var root = Path.GetFullPath(settings.RustServerPath.Trim());
        var exe = Path.Combine(root, "RustDedicated.exe");
        if (!File.Exists(exe)) throw new FileNotFoundException("RustDedicated.exe seçilen klasörde bulunamadı.", exe);
        ValidateHarmonyRuntime(root);

        var safeName = SettingsStore.SafeName(settings.MapName, $"Raven_{settings.WorldSize}_{settings.Seed}");
        var output = Path.Combine(SettingsStore.OutputRoot(settings), $"{safeName}_{settings.WorldSize}_{settings.Seed}");
        var backupPath = BackupExistingDelivery(settings, output, safeName);
        if (!string.IsNullOrWhiteSpace(backupPath))
            Log?.Invoke("Önceki sürüm otomatik yedeklendi.");

        Log?.Invoke("Harmony bileşenleri ve kurallar hazırlanıyor…");
        AssetStore.ExtractHarmonyMods(root);
        using var prefabSession = PrepareCustomPrefabs(root);
        WriteCustomGenerator(root, settings, prefabSession.InstalledCount > 0);
        if (prefabSession.InstalledCount > 0)
            Log?.Invoke($"Özel monument prefab sistemi aktif: {prefabSession.SelectedMonumentCount} monument / {prefabSession.InstalledCount} template.");
        else if (prefabSession.SelectedMonumentCount > 0)
            Log?.Invoke("UYARI: Seçilen özel prefab template'leri güncel Rust StringPool ile uyumlu değil. Harita bozulmaması için bu üretimde vanilla monument kullanılacak.");
        var identityDir = Path.Combine(root, "server", settings.Identity);
        Directory.CreateDirectory(identityDir);
        WriteWorldConfig(Path.Combine(identityDir, "harita_ayarlari.json"), settings);
        WritePlacementConfig(root, settings);
        Log?.Invoke("Kural politikası: God Rock kesin adet; diğer monumentler Rust doğal üretimi. Tek seed kullanılır ve harita her zaman teslim edilir.");

        var reports = Path.Combine(root, "HarmonyConfig", "RavenMapReports");
        Directory.CreateDirectory(reports);
        DeleteOldReport(Path.Combine(reports, $"RavenMapReport_{settings.WorldSize}_{settings.Seed}.json"));
        DeleteOldReport(Path.Combine(reports, $"RavenWorldObjectReport_{settings.WorldSize}_{settings.Seed}.json"));
        DeleteOldReport(Path.Combine(reports, $"RavenGodRockExactCount_{settings.WorldSize}_{settings.Seed}.json"));
        DeleteOldReport(Path.Combine(reports, "RavenGodRockExactCount_latest.json"));
        DeleteOldReport(Path.Combine(reports, $"RavenRequiredMonumentsValidation_{settings.WorldSize}_{settings.Seed}.json"));
        DeleteOldReport(Path.Combine(reports, "RavenRequiredMonumentsValidation_latest.json"));
        var mapExpected = Path.Combine(root, "maps", $"CustomGenerator{settings.WorldSize}_{settings.Seed}.map");
        var imageExpected = Path.Combine(root, "mapimages", $"CustomGenerator{settings.WorldSize}_{settings.Seed}.png");
        // A repeated seed must never complete against files left by an older run.
        DeleteOldReport(mapExpected);
        DeleteOldReport(imageExpected);

        var start = new ProcessStartInfo { FileName = exe, WorkingDirectory = root, UseShellExecute = false,
            RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true };
        foreach (var arg in new[] { "-batchmode", "-nographics", "+server.identity", settings.Identity,
            "+server.level", "Procedural Map", "+server.seed", settings.Seed.ToString(), "+server.worldsize", settings.WorldSize.ToString(),
            "+server.port", "28515", "+rcon.port", "28516", "+rcon.password", "raven_native", "+rcon.web", "true",
            "+server.hostname", "Raven Native Generator", "+world.configfile", "harita_ayarlari.json" }) start.ArgumentList.Add(arg);

        using var process = new Process { StartInfo = start, EnableRaisingEvents = true };
        process.OutputDataReceived += (_, e) => { if (!string.IsNullOrWhiteSpace(e.Data) && IsUseful(e.Data)) Log?.Invoke(e.Data); };
        process.ErrorDataReceived += (_, e) => { if (!string.IsNullOrWhiteSpace(e.Data)) Log?.Invoke(e.Data); };
        Log?.Invoke($"Harita üretiliyor — Seed {settings.Seed}, Boyut {settings.WorldSize}…");
        process.Start(); process.BeginOutputReadLine(); process.BeginErrorReadLine();
        try
        {
            var deadline = DateTime.UtcNow.AddMinutes(30);
            while (DateTime.UtcNow < deadline)
            {
                cancellation.ThrowIfCancellationRequested();
                if (File.Exists(mapExpected) && File.Exists(imageExpected) && await IsStable(mapExpected, cancellation) && await IsStable(imageExpected, cancellation)) break;
                if (process.HasExited && (!File.Exists(mapExpected) || !File.Exists(imageExpected)))
                    throw new InvalidOperationException($"Harita üretimi tamamlanamadı (çıkış kodu {process.ExitCode}).");
                await Task.Delay(2000, cancellation);
            }
            if (!File.Exists(mapExpected) || !File.Exists(imageExpected)) throw new TimeoutException("Harita üretimi 30 dakika içinde tamamlanmadı.");
        }
        finally
        {
            if (!process.HasExited) { try { process.Kill(true); } catch { } }
        }

        Log?.Invoke("Yeni ikonlar doğru koordinatlara işleniyor…");
        await WaitForReports(reports, settings, cancellation);
        Directory.CreateDirectory(output);
        var mapOut = Path.Combine(output, safeName + ".map");
        var imageOut = Path.Combine(output, safeName + ".png");
        File.Copy(mapExpected, mapOut, true);
        var counts = new OverlayService(catalog).Render(imageExpected, imageOut, reports, settings.WorldSize, settings.Seed);

        // Geçmiş ve karşılaştırma ekranının eski üretimlere de güvenebilmesi için
        // ham render ile bu seed'e ait raporları teslim klasörünün içine sabitliyoruz.
        var archivedRawImage = Path.Combine(output, "source.png");
        File.Copy(imageExpected, archivedRawImage, true);
        var archivedReports = Path.Combine(output, "Reports");
        SnapshotReports(reports, archivedReports, settings.WorldSize, settings.Seed);
        // Archive the effective settings of this job. In a multi-map queue the
        // project's on-disk seed remains the first seed, so copying that file
        // would attach incorrect settings to every later result.
        File.WriteAllText(Path.Combine(output, safeName + ".ravenmap"),
            JsonSerializer.Serialize(settings, new JsonSerializerOptions { WriteIndented = true }));

        Log?.Invoke("MAP ve PNG otomatik kaydedildi: " + output);
        var godRule = catalog.FirstOrDefault(x => x.Id.Equals("god_rocks", StringComparison.OrdinalIgnoreCase));
        if (godRule?.State == RuleState.Required)
        {
            var target = Math.Clamp(godRule.Minimum > 0 ? godRule.Minimum : 1, 1, 10);
            Log?.Invoke($"God Rock hedefi: {target} / doğrulanan: {counts.GodRocks} {(counts.GodRocks == target ? "✓" : "HATA")}");
        }
        else if (godRule?.State == RuleState.Blocked)
            Log?.Invoke($"God Rock hedefi: 0 / doğrulanan: {counts.GodRocks} {(counts.GodRocks == 0 ? "✓" : "HATA")}");
        Log?.Invoke($"Tamamlandı: {counts.Icons} ikon, {counts.GodRocks} doğrulanmış God Rock.");
        Log?.Invoke("Teslim modu: 1 seed. God Rock dışında post-map monument zorlama yok; eksik kurallar yalnız doğrulamada gösterilir.");
        return new(mapOut, imageOut, output, counts.Icons, counts.GodRocks,
            archivedRawImage, archivedReports, settings.WorldSize, settings.Seed, backupPath);
    }

    private static void ValidateHarmonyRuntime(string root)
    {
        var managed = Path.Combine(root, "RustDedicated_Data", "Managed");
        var harmony = Path.Combine(managed, "0Harmony.dll");
        var loader = Path.Combine(managed, "Rust.Harmony.dll");
        if (File.Exists(harmony) && File.Exists(loader)) return;

        throw new InvalidOperationException(
            "Seçilen Rust sunucusunda yerleşik Harmony çalışma dosyaları bulunamadı. " +
            "RustDedicated_Data\\Managed altında 0Harmony.dll ve Rust.Harmony.dll olmalı. " +
            "Sunucuyu SteamCMD ile güncelleyip doğruladıktan sonra tekrar deneyin. " +
            "Raven mod DLL'lerini elle kopyalamanız gerekmez; uygulama onları otomatik kurar.");
    }

    private static string BackupExistingDelivery(AppSettings settings, string output, string safeName)
    {
        try
        {
            if (!Directory.Exists(output) || !Directory.EnumerateFileSystemEntries(output).Any())
                return "";

            var backupRoot = Path.Combine(SettingsStore.OutputRoot(settings), "_Backups");
            Directory.CreateDirectory(backupRoot);
            var stamp = DateTime.Now.ToString("yyyy-MM-dd_HH-mm-ss");
            var backup = Path.Combine(backupRoot, $"{safeName}_{settings.WorldSize}_{settings.Seed}_{stamp}");
            var suffix = 1;
            while (Directory.Exists(backup))
                backup = Path.Combine(backupRoot, $"{safeName}_{settings.WorldSize}_{settings.Seed}_{stamp}_{suffix++}");

            CopyDirectory(output, Path.Combine(backup, "Harita"));
            return backup;
        }
        catch
        {
            // Yedekleme teslimi engellememeli. Üretim normal şekilde devam eder.
            return "";
        }
    }

    private static void CopyDirectory(string source, string destination)
    {
        Directory.CreateDirectory(destination);
        foreach (var file in Directory.EnumerateFiles(source))
            File.Copy(file, Path.Combine(destination, Path.GetFileName(file)), true);
        foreach (var directory in Directory.EnumerateDirectories(source))
            CopyDirectory(directory, Path.Combine(destination, Path.GetFileName(directory)));
    }

    private static void SnapshotReports(string source, string destination, int size, int seed)
    {
        Directory.CreateDirectory(destination);
        var names = new[]
        {
            $"RavenMapReport_{size}_{seed}.json",
            $"RavenWorldObjectReport_{size}_{seed}.json",
            $"RavenGodRockExactCount_{size}_{seed}.json",
            $"RavenRequiredMonumentsValidation_{size}_{seed}.json"
        };
        foreach (var name in names)
        {
            var file = Path.Combine(source, name);
            if (File.Exists(file))
                File.Copy(file, Path.Combine(destination, name), true);
        }
    }

    private static void WriteCustomGenerator(string root, AppSettings s, bool swapEnabled)
    {
        var folder = Path.Combine(root, "HarmonyConfig"); Directory.CreateDirectory(folder);
        var path = Path.Combine(folder, "CustomGenerator.json");
        JsonNode cfg;
        try { cfg = JsonNode.Parse(File.Exists(path) ? File.ReadAllText(path) : AssetStore.ReadText("config.CustomGenerator.default.json"))!; }
        catch { cfg = JsonNode.Parse(AssetStore.ReadText("config.CustomGenerator.default.json"))!; }

        var main = cfg["Main Generator"]!.AsObject();
        SetEnabled(main, "Road", s.Roads); SetEnabled(main, "Rail", s.Rails);
        main["Remove Rivers"] = !s.Rivers;
        main["Remove tunnel entrances"] = !s.UndergroundRails;
        main["Change percentages"] = true;

        if (main["UniqueEnviroment"] is JsonObject env)
        {
            env["ShouldChange"] = true;
            env["GenerateOasis"] = s.Oasis;
            env["GenerateCanyons"] = s.Canyons;
            env["GenerateLakes"] = s.Lakes;
        }

        if (main["Tier Percentages (100 in total)"] is not JsonObject tier)
            main["Tier Percentages (100 in total)"] = tier = [];
        tier["Tier0"] = ClampPercent(s.Tier0);
        tier["Tier1"] = ClampPercent(s.Tier1);
        tier["Tier2"] = ClampPercent(s.Tier2);

        const string biomeKey = "Bioms Percentages (100 in total) - idk why jungle 70%";
        if (main[biomeKey] is not JsonObject biome)
            main[biomeKey] = biome = [];
        biome["Arid"] = ClampPercent(s.BiomeArid);
        biome["Temperate"] = ClampPercent(s.BiomeTemperate);
        biome["Tundra"] = ClampPercent(s.BiomeTundra);
        biome["Arctic"] = ClampPercent(s.BiomeArctic);
        biome["Jungle"] = ClampPercent(s.BiomeJungle);

        if (cfg["Swap Monuments"] is not JsonObject swap)
            cfg["Swap Monuments"] = swap = [];
        swap["Enabled"] = swapEnabled;
        swap["Save both maps (with swap and without)"] = false;

        File.WriteAllText(path, cfg.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
    }

    private static double ClampPercent(double value) => Math.Clamp(double.IsFinite(value) ? value : 0d, 0d, 100d);

    private static void SetEnabled(JsonObject main, string key, bool value)
    {
        if (main[key] is not JsonObject obj) main[key] = obj = [];
        obj["ShouldChange"] = true; obj["Enabled"] = value; obj["GenerateRing"] = value; obj["GenerateSideMonuments"] = value;
    }

    private void WriteWorldConfig(string path, AppSettings s)
    {
        var blocked = catalog.Where(x => x.State == RuleState.Blocked).SelectMany(x => x.Prefabs.Concat(x.Aliases)).Distinct().ToArray();
        var data = new Dictionary<string, object> {
            ["MainRoads"] = s.Roads, ["SideRoads"] = s.Roads, ["Trails"] = s.Roads, ["Rivers"] = s.Rivers,
            ["Powerlines"] = s.PowerLines, ["AboveGroundRails"] = s.Rails, ["BelowGroundRails"] = s.UndergroundRails,
            ["UnderwaterLabs"] = catalog.FirstOrDefault(x => x.Id == "underwater_labs")?.State != RuleState.Blocked,
            ["GenerateLakes"] = s.Lakes, ["GenerateCanyons"] = s.Canyons, ["GenerateOasis"] = s.Oasis,
            ["PercentageTier0"] = ClampPercent(s.Tier0) / 100d, ["PercentageTier1"] = ClampPercent(s.Tier1) / 100d, ["PercentageTier2"] = ClampPercent(s.Tier2) / 100d,
            ["PercentageBiomeArid"] = ClampPercent(s.BiomeArid) / 100d, ["PercentageBiomeTemperate"] = ClampPercent(s.BiomeTemperate) / 100d, ["PercentageBiomeTundra"] = ClampPercent(s.BiomeTundra) / 100d,
            ["PercentageBiomeArctic"] = ClampPercent(s.BiomeArctic) / 100d, ["PercentageBiomeJungle"] = ClampPercent(s.BiomeJungle) / 100d,
            ["PrefabBlacklist"] = blocked, ["PrefabWhitelist"] = Array.Empty<string>()
        };
        File.WriteAllText(path, JsonSerializer.Serialize(data, new JsonSerializerOptions { WriteIndented = true }));
    }

    private void WritePlacementConfig(string root, AppSettings s)
    {
        var rules = catalog.Where(x => x.State is RuleState.Required or RuleState.Blocked).Select(x => new {
            id = x.Id, name = x.Name, state = x.State == RuleState.Required ? "required" : "blocked",
            min = x.State == RuleState.Required ? Math.Max(1, x.Minimum) : 0,
            max = x.State == RuleState.Blocked ? 0 : (x.Id == "god_rocks" ? Math.Max(1, x.Minimum) : Math.Max(Math.Max(1, x.Minimum), x.Maximum)),
            prefabMarkers = x.Prefabs.Concat(x.Aliases).Distinct().ToArray(), spawnPrefab = SpawnPrefab(x.Id, x.Prefabs),
            placement = x.Id == "god_rocks" ? "large_rock_root_replace" : "native_generator_only",
            // v3.5.2: normal monuments are NEVER injected/removed after generation.
            // God Rock still uses its dedicated verified root-slot controller before Save.
            generatorOnly = true,
            minDistance = 300, flatRadius = 80, maxHeightDelta = 18
        }).ToArray();
        var folder = Path.Combine(root, "HarmonyConfig"); Directory.CreateDirectory(folder);
        var payload = new { enabled = rules.Any(), singleSeed = true, maxSeedAttempts = 1, alwaysDeliver = true,
            jobId = $"native-{DateTime.UtcNow:yyyyMMddHHmmss}", seed = s.Seed, worldSize = s.WorldSize,
            auditPath = Path.Combine(folder, "RavenGuaranteedPlacementAudits", $"native_{s.WorldSize}_{s.Seed}.json"),
            mapPath = Path.Combine(root, "maps", $"CustomGenerator{s.WorldSize}_{s.Seed}.map"), rules };
        File.WriteAllText(Path.Combine(folder, "RavenGuaranteedPlacement.json"), JsonSerializer.Serialize(payload, new JsonSerializerOptions { WriteIndented = true }));
    }


    private static string PlacementMode(string id) => id switch
    {
        "god_rocks" => "large_rock_root_replace",
        "oilrig_large" or "oilrig_small" => "offshore",
        "underwater_labs" => "underwater",
        "harbor" or "ferry_terminal" or "fishing_village" or "lighthouse" => "coast",
        _ => "land"
    };

    // These categories are generated from roads/topology/noise networks rather than a
    // standalone monument root. Raven validates them in the same seed but never
    // injects them blindly because doing so can corrupt terrain/path dependencies.
    private static bool IsValidationOnly(string id) => id is
        "god_rocks" or "power_substations" or "powerlines" or "metro_entrances" or
        "icebergs" or "caves" or "swamps" or "ruins" or "lakes" or "canyons" or "oases";

    private static int PlacementMinDistance(string id) => id switch
    {
        "launch_site" => 520,
        "airfield" => 470,
        "excavator" => 430,
        "military_tunnels" => 400,
        "trainyard" => 390,
        "powerplant" or "water_treatment" => 360,
        "oilrig_large" or "oilrig_small" or "underwater_labs" => 420,
        "harbor" or "ferry_terminal" => 320,
        _ => 300
    };

    private static int PlacementFlatRadius(string id) => id switch
    {
        "launch_site" => 190,
        "airfield" => 175,
        "excavator" => 160,
        "military_tunnels" => 145,
        "trainyard" => 140,
        "powerplant" or "water_treatment" => 130,
        "desert_military_base" or "arctic_research_base" => 120,
        "nuclear_silo" or "outpost" or "bandit_camp" => 105,
        "harbor" or "ferry_terminal" => 95,
        _ => 80
    };

    private static int PlacementMaxHeightDelta(string id) => id switch
    {
        "launch_site" or "airfield" => 12,
        "excavator" or "military_tunnels" or "trainyard" => 14,
        "powerplant" or "water_treatment" => 16,
        _ => 18
    };

    private static string SpawnPrefab(string id, List<string> prefabs)
    {
        var direct = prefabs.FirstOrDefault(x => x.StartsWith("assets/", StringComparison.OrdinalIgnoreCase));
        if (direct is not null) return direct;
        return id switch {
            "military_tunnels" => "assets/bundled/prefabs/autospawn/monument/large/military_tunnel_1.prefab",
            "powerplant" => "assets/bundled/prefabs/autospawn/monument/large/powerplant_1.prefab",
            "water_treatment" => "assets/bundled/prefabs/autospawn/monument/large/water_treatment_plant_1.prefab",
            "excavator" => "assets/bundled/prefabs/autospawn/monument/large/excavator_1.prefab",
            "junkyard" => "assets/bundled/prefabs/autospawn/monument/medium/junkyard_1.prefab",
            "nuclear_silo" => "assets/bundled/prefabs/autospawn/monument/medium/nuclear_missile_silo.prefab",
            "arctic_research_base" => "assets/bundled/prefabs/autospawn/monument/arctic_bases/arctic_research_base_a.prefab",
            "desert_military_base" => "assets/bundled/prefabs/autospawn/monument/military_bases/desert_military_base_a.prefab",
            "ferry_terminal" => "assets/bundled/prefabs/autospawn/monument/harbor/ferry_terminal_1.prefab",
            "satellite_dish" => "assets/bundled/prefabs/autospawn/monument/small/satellite_dish.prefab",
            "dome" => "assets/bundled/prefabs/autospawn/monument/small/sphere_tank.prefab",
            "ziggurat" => "assets/bundled/prefabs/autospawn/monument/jungle_ruins/jungle_ziggurat_a.prefab",
            "radtown" => "assets/bundled/prefabs/autospawn/monument/roadside/radtown_1.prefab",
            "supermarket" => "assets/bundled/prefabs/autospawn/monument/roadside/supermarket_1.prefab",
            "gas_station" => "assets/bundled/prefabs/autospawn/monument/roadside/gas_station_1.prefab",
            "sewer_branch" => "assets/bundled/prefabs/autospawn/monument/medium/radtown_small_3.prefab",
            "warehouse" => "assets/bundled/prefabs/autospawn/monument/roadside/warehouse.prefab",
            "outpost" => "assets/bundled/prefabs/autospawn/monument/medium/compound.prefab",
            "bandit_camp" => "assets/bundled/prefabs/autospawn/monument/medium/bandit_town.prefab",
            "stables" => "assets/bundled/prefabs/autospawn/monument/small/stables_a.prefab",
            "harbor" => "assets/bundled/prefabs/autospawn/monument/harbor/harbor_1.prefab",
            "fishing_village" => "assets/bundled/prefabs/autospawn/monument/fishing_village/fishing_village_a.prefab",
            "lighthouse" => "assets/bundled/prefabs/autospawn/monument/lighthouse/lighthouse.prefab",
            "stone_quarry" => "assets/bundled/prefabs/autospawn/monument/small/mining_quarry_b.prefab",
            "sulfur_quarry" => "assets/bundled/prefabs/autospawn/monument/small/mining_quarry_a.prefab",
            "hqm_quarry" => "assets/bundled/prefabs/autospawn/monument/small/mining_quarry_c.prefab",
            _ => ""
        };
    }


    private CustomPrefabSession PrepareCustomPrefabs(string root)
    {
        var selected = catalog.Where(x => x.CustomPrefabAvailable && x.UseCustomPrefab && x.State != RuleState.Blocked).ToList();
        var targetDir = Path.Combine(root, "maps", "prefabs");
        var backupDir = Path.Combine(root, "HarmonyConfig", "RavenPrefabBackups",
            $"{DateTime.UtcNow:yyyyMMddHHmmss}_{Guid.NewGuid():N}");
        Directory.CreateDirectory(targetDir);
        Directory.CreateDirectory(backupDir);

        var session = new CustomPrefabSession(backupDir) { SelectedMonumentCount = selected.Count };

        try
        {
            foreach (var existing in Directory.EnumerateFiles(targetDir, "*.map", SearchOption.TopDirectoryOnly).ToArray())
            {
                var backup = Path.Combine(backupDir, Path.GetFileName(existing));
                File.Copy(existing, backup, true);
                File.Delete(existing);
                session.Backups.Add((existing, backup));
            }

            foreach (var rule in selected)
            {
                foreach (var template in CustomPrefabTemplates(rule.Id))
                {
                    var destination = Path.Combine(targetDir, template.Target);
                    AssetStore.CopyEmbedded("custom_prefabs." + template.Resource, destination);

                    var invalidIds = FindKnownInvalidPrefabIds(destination);
                    if (invalidIds.Count > 0)
                    {
                        try { File.Delete(destination); } catch { }
                        session.InvalidTemplates.Add(new InvalidCustomPrefabTemplate(
                            rule.Id, rule.Name, template.Resource, template.Target, invalidIds));
                        Log?.Invoke($"UYARI: {rule.Name} özel prefabı güncel Rust ile uyumsuz (geçersiz prefab ID: {string.Join(", ", invalidIds)}). Bu seed için vanilla {rule.Name} kullanılacak.");
                        continue;
                    }

                    session.Installed.Add(destination);
                }
            }

            var reportFolder = Path.Combine(root, "HarmonyConfig", "RavenMapReports");
            Directory.CreateDirectory(reportFolder);
            var audit = new
            {
                format = "raven-custom-prefab-session-v1",
                generatedAtUtc = DateTime.UtcNow,
                selectedMonuments = selected.Select(x => new { x.Id, x.Name }).ToArray(),
                installedTemplates = session.Installed.Select(x => Path.GetFileName(x)).ToArray(),
                invalidTemplates = session.InvalidTemplates.Select(x => new
                {
                    x.MonumentId, x.MonumentName, x.Resource, x.Target, invalidPrefabIds = x.InvalidPrefabIds
                }).ToArray(),
                fallbackToVanilla = session.InvalidTemplates.Count > 0,
                quarantinedExistingTemplates = session.Backups.Select(x => Path.GetFileName(x.Original)).ToArray(),
                swapEnabled = session.Installed.Count > 0,
                note = "Invalid bundled custom prefab templates are never installed. The current seed is still generated and the affected monument stays vanilla. maps/prefabs is isolated for one generation and restored after the map job."
            };
            File.WriteAllText(Path.Combine(reportFolder, "RavenCustomPrefabAudit_latest.json"),
                JsonSerializer.Serialize(audit, new JsonSerializerOptions { WriteIndented = true }));

            return session;
        }
        catch
        {
            session.Dispose();
            throw;
        }
    }


    private static readonly uint[] KnownInvalidPrefabIds =
    [
        2630900005u,
        924385556u,
        3921325388u,
        1883306314u
    ];

    private static List<uint> FindKnownInvalidPrefabIds(string path)
    {
        var data = File.ReadAllBytes(path);
        var found = new List<uint>();
        foreach (var id in KnownInvalidPrefabIds)
        {
            var encoded = EncodeVarUInt32(id);
            if (ContainsSequence(data, encoded)) found.Add(id);
        }
        return found;
    }

    private static byte[] EncodeVarUInt32(uint value)
    {
        var bytes = new List<byte>(5);
        while (value >= 0x80)
        {
            bytes.Add((byte)(value | 0x80));
            value >>= 7;
        }
        bytes.Add((byte)value);
        return bytes.ToArray();
    }

    private static bool ContainsSequence(byte[] haystack, byte[] needle)
    {
        if (needle.Length == 0 || haystack.Length < needle.Length) return false;
        for (var i = 0; i <= haystack.Length - needle.Length; i++)
        {
            var match = true;
            for (var j = 0; j < needle.Length; j++)
            {
                if (haystack[i + j] == needle[j]) continue;
                match = false;
                break;
            }
            if (match) return true;
        }
        return false;
    }

    private sealed record InvalidCustomPrefabTemplate(
        string MonumentId, string MonumentName, string Resource, string Target, IReadOnlyList<uint> InvalidPrefabIds);

    private static IReadOnlyList<(string Resource, string Target)> CustomPrefabTemplates(string id) => id switch
    {
        "outpost" => [("rustmaps_outpost_template.map", "compound.prefab.map")],
        "bandit_camp" => [("rustmaps_bandit_camp_template.map", "bandit_town.prefab.map")],
        "stables" => [
            ("rustmaps_stables_a_template.map", "stables_a.prefab.map"),
            ("rustmaps_stables_b_template.map", "stables_b.prefab.map")
        ],
        "fishing_village" => [
            ("rustmaps_fishing_village_a_template.map", "fishing_village_a.prefab.map"),
            ("rustmaps_fishing_village_b_template.map", "fishing_village_b.prefab.map"),
            ("rustmaps_fishing_village_c_template.map", "fishing_village_c.prefab.map")
        ],
        _ => []
    };

    private sealed class CustomPrefabSession(string backupDir) : IDisposable
    {
        private bool disposed;
        public int SelectedMonumentCount { get; set; }
        public int InstalledCount => Installed.Count;
        public List<string> Installed { get; } = [];
        public List<InvalidCustomPrefabTemplate> InvalidTemplates { get; } = [];
        public List<(string Original, string Backup)> Backups { get; } = [];

        public void Dispose()
        {
            if (disposed) return;
            disposed = true;
            foreach (var installed in Installed)
            {
                try { if (File.Exists(installed)) File.Delete(installed); } catch { }
            }
            foreach (var item in Backups)
            {
                try
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(item.Original)!);
                    if (File.Exists(item.Backup)) File.Copy(item.Backup, item.Original, true);
                }
                catch { }
            }
            try { if (Directory.Exists(backupDir)) Directory.Delete(backupDir, true); } catch { }
        }
    }

    private static void DeleteOldReport(string path) { try { if (File.Exists(path)) File.Delete(path); } catch { } }
    private static async Task<bool> IsStable(string path, CancellationToken token) { var a = new FileInfo(path).Length; await Task.Delay(1200, token); return File.Exists(path) && new FileInfo(path).Length == a && a > 1024; }
    private static async Task WaitForReports(string folder, AppSettings s, CancellationToken token)
    {
        var end = DateTime.UtcNow.AddSeconds(45);
        var mapReport = Path.Combine(folder, $"RavenMapReport_{s.WorldSize}_{s.Seed}.json");
        var worldReport = Path.Combine(folder, $"RavenWorldObjectReport_{s.WorldSize}_{s.Seed}.json");
        while (DateTime.UtcNow < end)
        {
            if (File.Exists(mapReport) && File.Exists(worldReport)) return;
            await Task.Delay(1000, token);
        }
    }
    private static bool IsUseful(string line) => line.Contains("Raven", StringComparison.OrdinalIgnoreCase) || line.Contains("CustomGenerator", StringComparison.OrdinalIgnoreCase)
        || line.Contains("Generating", StringComparison.OrdinalIgnoreCase) || line.Contains("Map", StringComparison.OrdinalIgnoreCase);
}
