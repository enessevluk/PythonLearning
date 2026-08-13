using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Media.Imaging;

namespace RavenMapPanel;

public abstract class NotifyBase : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;
    protected void Changed([CallerMemberName] string? name = null) => PropertyChanged?.Invoke(this, new(name));
}

public sealed class MonumentRuleViewModel : NotifyBase
{
    public MonumentRule Rule { get; }
    public string Id => Rule.Id;
    public string Name => Rule.Name;
    public string Category => Rule.Category;
    public string IconPath => AssetStore.IconSource(Rule.Icon);
    public string SourceLabel => Rule.IsCustom ? "Özel" : "Raven";
    public bool IsGodRock => string.Equals(Id, "god_rocks", System.StringComparison.OrdinalIgnoreCase);
    public bool CustomPrefabAvailable => Rule.CustomPrefabAvailable;
    public bool UseCustomPrefab
    {
        get => Rule.UseCustomPrefab;
        set
        {
            var next = CustomPrefabAvailable && State == RuleState.Required && value;
            if (Rule.UseCustomPrefab == next) return;
            Rule.UseCustomPrefab = next;
            Changed();
            Changed(nameof(PrefabModeText));
            Changed(nameof(PrefabModeColor));
        }
    }
    public string PrefabModeText => UseCustomPrefab ? "Özel prefab" : "Vanilla";
    public string PrefabModeColor => UseCustomPrefab ? "#62D8BD" : "#8A9BAB";
    public int TargetCount
    {
        get => IsGodRock ? System.Math.Clamp(Rule.Minimum > 0 ? Rule.Minimum : 3, 1, 10) : Rule.Minimum;
        set
        {
            if (!IsGodRock) return;
            var next = System.Math.Clamp(value, 1, 10);
            if (Rule.Minimum == next && Rule.Maximum == next) return;
            Rule.Minimum = next; Rule.Maximum = next; Changed();
        }
    }
    public RuleState State { get => Rule.State; set { if (Rule.State == value) return; Rule.State = value; if (value == RuleState.Required && Rule.Minimum == 0) { Rule.Minimum = IsGodRock ? 3 : 1; if (IsGodRock) Rule.Maximum = Rule.Minimum; } if (value != RuleState.Required && Rule.UseCustomPrefab) Rule.UseCustomPrefab = false; Changed(); Changed(nameof(StateSymbol)); Changed(nameof(StateText)); Changed(nameof(StateColor)); Changed(nameof(StateBackground)); Changed(nameof(StateBorderColor)); Changed(nameof(TargetCount)); Changed(nameof(UseCustomPrefab)); Changed(nameof(PrefabModeText)); Changed(nameof(PrefabModeColor)); } }
    public string StateSymbol => State switch { RuleState.Required => "✓", RuleState.Blocked => "×", _ => "?" };
    public string StateText => State switch { RuleState.Required => IsGodRock ? "Kesin olsun" : "Olsun", RuleState.Blocked => "Olmasın", _ => "İsteğe bağlı" };
    public string StateColor => State switch { RuleState.Required => "#59E39A", RuleState.Blocked => "#FF737A", _ => "#FFD35A" };
    public string StateBackground => State switch { RuleState.Required => "#173B2D", RuleState.Blocked => "#401F25", _ => "#403719" };
    public string StateBorderColor => State switch { RuleState.Required => "#2C9B65", RuleState.Blocked => "#B84B54", _ => "#B49532" };
    public MonumentRuleViewModel(MonumentRule rule) => Rule = rule;
    public void NotifyRuleValuesChanged() { Changed(nameof(TargetCount)); Changed(nameof(UseCustomPrefab)); Changed(nameof(PrefabModeText)); Changed(nameof(PrefabModeColor)); }
    public void Cycle() => State = State switch { RuleState.Optional => RuleState.Required, RuleState.Required => RuleState.Blocked, _ => RuleState.Optional };
}

public sealed record MapMarker(string Category, string Name, string Source, double X, double Z, string IconFile);

public sealed class MapDocument
{
    public string ImagePath { get; init; } = "";
    public int ImageWidth { get; init; }
    public int ImageHeight { get; init; }
    public int WorldSize { get; init; }
    public int MapResolution { get; init; }
    public int RenderOffset { get; init; }
    public ObservableCollection<MapMarker> Markers { get; } = [];
    public BitmapImage? Image { get; set; }
    public System.Windows.Point Project(double x, double z)
    {
        var px = RenderOffset + ((x + WorldSize / 2d) / WorldSize) * MapResolution;
        // Rust world +Z points upward on the generated map while bitmap Y grows
        // downward. The vertical axis must therefore be inverted.
        var py = RenderOffset + ((WorldSize / 2d - z) / WorldSize) * MapResolution;
        return new(px * ImageWidth / Math.Max(1d, ImageWidth), py * ImageHeight / Math.Max(1d, ImageHeight));
    }
}
