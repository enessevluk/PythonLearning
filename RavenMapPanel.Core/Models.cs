using System.Text.Json.Serialization;

namespace RavenMapPanel;

public enum RuleState { Optional, Required, Blocked }

public sealed class MonumentRule
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string Category { get; set; } = "";
    public string Icon { get; set; } = "";
    public bool RenderOnMap { get; set; } = true;
    public List<string> Prefabs { get; set; } = [];
    public List<string> Aliases { get; set; } = [];
    public bool IsCustom { get; set; }
    public bool CustomPrefabAvailable { get; set; }
    [JsonIgnore] public bool UseCustomPrefab { get; set; }
    [JsonIgnore] public RuleState State { get; set; }
    [JsonIgnore] public int Minimum { get; set; }
    [JsonIgnore] public int Maximum { get; set; } = 99;
}

public sealed class AppSettings
{
    public string RustServerPath { get; set; } = @"C:\RustMapServer\RustServer";
    public string Identity { get; set; } = "raven_native";
    public string MapName { get; set; } = "RavenHarita";
    public int Seed { get; set; } = Random.Shared.Next(1, int.MaxValue);
    public int WorldSize { get; set; } = 4500;
    public int GenerationCount { get; set; } = 1;
    public bool Roads { get; set; } = true;
    public bool Rivers { get; set; } = true;
    public bool PowerLines { get; set; } = true;
    public bool Rails { get; set; } = true;
    public bool UndergroundRails { get; set; } = true;
    public bool Oasis { get; set; }
    public bool Canyons { get; set; }
    public bool Lakes { get; set; }

    // Advanced CustomGenerator percentages. Tier values are relative weights and
    // are normalized by CustomGenerator. Main biome values are corrected by
    // RavenBiomeFix; Jungle remains an independent conversion ratio.
    public double Tier0 { get; set; } = 30;
    public double Tier1 { get; set; } = 30;
    public double Tier2 { get; set; } = 40;
    public double BiomeArid { get; set; } = 40;
    public double BiomeTemperate { get; set; } = 15;
    public double BiomeTundra { get; set; } = 15;
    public double BiomeArctic { get; set; } = 30;
    public double BiomeJungle { get; set; } = 70;

    public double IconSize { get; set; } = 42;
    public bool ShowRavenIcons { get; set; } = true;
    public bool ShowEnvironmentIcons { get; set; } = true;
    public string CurrentProjectPath { get; set; } = "";
    public string MapsOutputPath { get; set; } = "";
    public List<string> RecentProjects { get; set; } = [];
    public Dictionary<string, RuleSetting> Rules { get; set; } = [];
}

public sealed class RuleSetting
{
    public RuleState State { get; set; }
    public int Minimum { get; set; }
    public int Maximum { get; set; } = 99;
    public bool UseCustomPrefab { get; set; }
}

public sealed record MapEntry(string Category, string Name, string RawName, string Source,
    double X, double Y, double Z, double Confidence = 0);

public sealed record GenerationResult(string MapPath, string ImagePath, string OutputFolder,
    int IconCount, int GodRockCount, string RawImagePath = "", string ReportFolder = "", int WorldSize = 0, int Seed = 0,
    string BackupPath = "");

public sealed class GenerationHistoryEntry
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string MapName { get; set; } = "RavenHarita";
    public int Seed { get; set; }
    public int WorldSize { get; set; }
    public DateTime GeneratedAt { get; set; } = DateTime.Now;
    public int IconCount { get; set; }
    public int GodRockCount { get; set; }
    public int ValidationIssues { get; set; }
    public string MapPath { get; set; } = "";
    public string ImagePath { get; set; } = "";
    public string RawImagePath { get; set; } = "";
    public string ReportFolder { get; set; } = "";
    public string OutputFolder { get; set; } = "";
    public string ProjectPath { get; set; } = "";
    public string BackupPath { get; set; } = "";

    [JsonIgnore] public string DisplayName => $"{MapName} · {WorldSize} · {Seed}";
    [JsonIgnore] public string GeneratedText => GeneratedAt.ToString("dd.MM.yyyy · HH:mm");
    [JsonIgnore] public string SummaryText => $"{IconCount} ikon · God Rock {GodRockCount}";
    [JsonIgnore] public string ValidationText => ValidationIssues == 0 ? "Doğrulama başarılı" : $"{ValidationIssues} sorun";
    [JsonIgnore] public string ValidationColor => ValidationIssues == 0 ? "#6FDBAE" : "#FF6868";
    [JsonIgnore] public string BackupText => string.IsNullOrWhiteSpace(BackupPath) ? "" : "Önceki sürüm yedeklendi";
    [JsonIgnore] public bool HasBackup => !string.IsNullOrWhiteSpace(BackupPath) && Directory.Exists(BackupPath);
}

public sealed class MapComparisonRow
{
    public string Name { get; set; } = "";
    public int CountA { get; set; }
    public int CountB { get; set; }
    [JsonIgnore] public int Difference => CountB - CountA;
    [JsonIgnore] public string DifferenceText => Difference == 0 ? "—" : Difference > 0 ? $"+{Difference}" : Difference.ToString();
    [JsonIgnore] public string DifferenceColor => Difference == 0 ? "#7F8B98" : Difference > 0 ? "#6FDBAE" : "#FF6868";
}
