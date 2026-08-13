using Microsoft.Win32;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Button = System.Windows.Controls.Button;
using TextBox = System.Windows.Controls.TextBox;
using MessageBox = System.Windows.MessageBox;
using OpenFileDialog = Microsoft.Win32.OpenFileDialog;
using SaveFileDialog = Microsoft.Win32.SaveFileDialog;

namespace RavenMapPanel;

public partial class MainWindow : Window
{
    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int valueSize);

    private AppSettings settings = SettingsStore.Load();
    private readonly List<MonumentRule> catalog = AssetStore.LoadCatalog();
    public ObservableCollection<MonumentRuleViewModel> MonumentItems { get; } = [];
    public IEnumerable<MonumentRuleViewModel> CustomPrefabItems => MonumentItems.Where(x => x.CustomPrefabAvailable);
    public ICollectionView MonumentView { get; }
    private readonly DispatcherTimer saveTimer = new() { Interval = TimeSpan.FromMilliseconds(500) };
    private readonly DispatcherTimer heroTimer = new() { Interval = TimeSpan.FromSeconds(8) };
    private readonly string[] heroBackgrounds = ["arkaplan-2.jpg", "arkaplan-3.jpg", "arkaplan-4.jpg", "arkaplan-5.jpg"];
    private int heroIndex = 3;
    private CancellationTokenSource? cancellation;
    private GenerationResult? result;
    private string searchText = "";
    private string selectedCategory = "Tümü";
    private string selectedStateFilter = "Tümü";
    private MapMarker? selectedMapMarker;
    private MonumentRule? selectedGalleryRule;
    private List<MonumentGalleryImage> markerGallery = [];
    private int markerGalleryIndex;
    public ObservableCollection<GenerationHistoryEntry> GenerationHistoryItems { get; } = [];
    public ObservableCollection<MapComparisonRow> ComparisonItems { get; } = [];
    private readonly JsonSerializerOptions jsonOptions = new() { WriteIndented = true, PropertyNameCaseInsensitive = true };

    public MainWindow()
    {
        foreach (var rule in catalog.OrderBy(x => x.Category).ThenBy(x => x.Name)) MonumentItems.Add(new(rule));
        MonumentView = CollectionViewSource.GetDefaultView(MonumentItems); MonumentView.Filter = FilterRule;
        InitializeComponent(); DataContext = this;
        CategoryBox.Items.Add("Tümü"); foreach (var c in catalog.Select(x => x.Category).Distinct().OrderBy(x => x)) CategoryBox.Items.Add(c); CategoryBox.SelectedIndex = 0;
        foreach (var state in new[] { "Tümü", "Olsun", "Olmasın", "İsteğe bağlı" }) StateFilterBox.Items.Add(state);
        StateFilterBox.SelectedIndex = 0;
        HistoryList.ItemsSource = GenerationHistoryItems;
        CompareABox.ItemsSource = GenerationHistoryItems;
        CompareBBox.ItemsSource = GenerationHistoryItems;
        ComparisonList.ItemsSource = ComparisonItems;
        MapView.ZoomChanged += value => ZoomLabel.Text = $"{value:0}%";
        MapView.MarkerSelected += OnMapMarkerSelected;
        saveTimer.Tick += (_, _) => { saveTimer.Stop(); SyncUiToSettings(); SettingsStore.Save(settings); };
        heroTimer.Tick += (_, _) => StepHero(1);
        heroTimer.Start();
        UpdateHeroBackground();
        LoadSettingsIntoUi();
        RefreshHistory();
        Navigate("Dashboard");
        Closing += (_, _) => { heroTimer.Stop(); SyncUiToSettings(); SettingsStore.Save(settings); };
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        var enabled = 1;
        var handle = new System.Windows.Interop.WindowInteropHelper(this).Handle;
        _ = DwmSetWindowAttribute(handle, 20, ref enabled, sizeof(int));
    }

    private void Navigate_Click(object sender, RoutedEventArgs e) => Navigate(Convert.ToString(((Button)sender).Tag) ?? "Dashboard");
    private void GoStudio_Click(object sender, RoutedEventArgs e) => Navigate("Studio");
    private void GoMonuments_Click(object sender, RoutedEventArgs e) => Navigate("Monuments");
    private void Navigate(string page)
    {
        DashboardPage.Visibility = page == "Dashboard" ? Visibility.Visible : Visibility.Collapsed;
        StudioPage.Visibility = page == "Studio" ? Visibility.Visible : Visibility.Collapsed;
        MonumentsPage.Visibility = page == "Monuments" ? Visibility.Visible : Visibility.Collapsed;
        IconsPage.Visibility = page == "Icons" ? Visibility.Visible : Visibility.Collapsed;
        AdvancedPage.Visibility = page == "Advanced" ? Visibility.Visible : Visibility.Collapsed;
        HistoryPage.Visibility = page == "History" ? Visibility.Visible : Visibility.Collapsed;
        ValidationPage.Visibility = page == "Validation" ? Visibility.Visible : Visibility.Collapsed;
        SettingsPage.Visibility = page == "Settings" ? Visibility.Visible : Visibility.Collapsed;
        LogsPage.Visibility = page == "Logs" ? Visibility.Visible : Visibility.Collapsed;
        if (page == "History") RefreshHistory();

        var navButtons = new[] { NavDashboard, NavStudio, NavMonuments, NavIcons, NavAdvanced, NavHistory, NavValidation, NavLogs, NavSettings };
        foreach (var button in navButtons)
        {
            var active = string.Equals(Convert.ToString(button.Tag), page, StringComparison.OrdinalIgnoreCase);
            button.Background = active
                ? new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x23, 0x1A, 0x17))
                : System.Windows.Media.Brushes.Transparent;
            button.BorderBrush = active
                ? new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0xA8, 0x45, 0x29))
                : System.Windows.Media.Brushes.Transparent;
            button.Foreground = active
                ? new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0xF7, 0xD5, 0xC8))
                : (System.Windows.Media.Brush)FindResource("TextBrush");
        }

        BottomStatus.Text = page switch
        {
            "Dashboard" => "Başlangıç",
            "Studio" => "Harita Oluştur",
            "Monuments" => "Monument Kuralları",
            "Icons" => "İkonlar ve Katmanlar",
            "Advanced" => "Biyom ve Dünya",
            "History" => "Harita Geçmişi",
            "Validation" => "Harita Doğrulama",
            "Settings" => "Uygulama Ayarları",
            _ => "Üretim Logları"
        };
    }

    private void LoadSettingsIntoUi()
    {
        MapNameBox.Text = settings.MapName; SeedBox.Text = settings.Seed.ToString(); SizeBox.Text = settings.WorldSize.ToString(); RustPathBox.Text = settings.RustServerPath;
        GenerationCountBox.SelectedIndex = Math.Clamp(settings.GenerationCount, 1, 7) - 1;
        RoadsCheck.IsChecked = settings.Roads; RiversCheck.IsChecked = settings.Rivers; PowerCheck.IsChecked = settings.PowerLines; RailsCheck.IsChecked = settings.Rails; MetroCheck.IsChecked = settings.UndergroundRails;
        OasisCheck.IsChecked = settings.Oasis; CanyonCheck.IsChecked = settings.Canyons; LakesCheck.IsChecked = settings.Lakes;
        Tier0Slider.Value = settings.Tier0; Tier1Slider.Value = settings.Tier1; Tier2Slider.Value = settings.Tier2;
        BiomeAridSlider.Value = settings.BiomeArid; BiomeTemperateSlider.Value = settings.BiomeTemperate; BiomeTundraSlider.Value = settings.BiomeTundra; BiomeArcticSlider.Value = settings.BiomeArctic; BiomeJungleSlider.Value = settings.BiomeJungle;
        UpdateAdvancedLabels();
        foreach (var vm in MonumentItems)
        {
            if (settings.Rules.TryGetValue(vm.Id, out var saved))
            {
                vm.Rule.Minimum = saved.Minimum; vm.Rule.Maximum = saved.Maximum; vm.State = saved.State;
                vm.UseCustomPrefab = saved.UseCustomPrefab;
                vm.NotifyRuleValuesChanged();
            }
            else
            {
                vm.State = RuleState.Optional; vm.UseCustomPrefab = false; vm.NotifyRuleValuesChanged();
            }
        }
        IconSizeSlider.Value = settings.IconSize; ShowIconsCheck.IsChecked = settings.ShowRavenIcons; ShowEnvironmentCheck.IsChecked = settings.ShowEnvironmentIcons;
        MapView.IconSize = settings.IconSize; MapView.ShowIcons = settings.ShowRavenIcons; MapView.ShowEnvironment = settings.ShowEnvironmentIcons;
        OutputPathBox.Text = SettingsStore.OutputRoot(settings);
        DefaultMapsText.Text = "Varsayılan konum: " + SettingsStore.DefaultMapsRoot;
        CurrentProjectText.Text = string.IsNullOrWhiteSpace(settings.CurrentProjectPath) ? "Henüz kaydedilmedi." : settings.CurrentProjectPath;
        SettingsActiveMapName.Text = string.IsNullOrWhiteSpace(settings.MapName) ? "RavenHarita" : settings.MapName;
        ProjectTitle.Text = string.IsNullOrWhiteSpace(settings.MapName) ? "Yeni harita" : settings.MapName;
        SidebarProjectName.Text = string.IsNullOrWhiteSpace(settings.MapName) ? "Yeni Raven Haritası" : settings.MapName;
        SidebarProjectPath.Text = string.IsNullOrWhiteSpace(settings.CurrentProjectPath) ? "Henüz kaydedilmedi" : "Config'e kaydedildi";
        SidebarProjectPath.ToolTip = string.IsNullOrWhiteSpace(settings.CurrentProjectPath) ? SettingsStore.ConfigRoot : settings.CurrentProjectPath;
        RecentProjectsList.ItemsSource = settings.RecentProjects.Where(File.Exists).Take(8).Select(path => new RecentProjectEntry(
            Path.GetFileNameWithoutExtension(path),
            File.GetLastWriteTime(path).ToString("dd.MM.yyyy · HH:mm"),
            Path.GetDirectoryName(path)?.Equals(SettingsStore.ConfigRoot, StringComparison.OrdinalIgnoreCase) == true ? "Config klasörü" : Path.GetDirectoryName(path) ?? "Ayar klasörü",
            path)).ToList();
        UpdateSummaries();
    }

    private void SyncUiToSettings()
    {
        settings.MapName = string.IsNullOrWhiteSpace(MapNameBox.Text) ? "RavenHarita" : MapNameBox.Text.Trim();
        if (int.TryParse(SeedBox.Text, out var seed) && seed > 0) settings.Seed = seed;
        if (int.TryParse(SizeBox.Text, out var size) && size is >= 1000 and <= 6000) settings.WorldSize = size;
        settings.GenerationCount = Math.Clamp(GenerationCountBox.SelectedIndex + 1, 1, 7);
        settings.RustServerPath = RustPathBox.Text.Trim(); settings.Roads = RoadsCheck.IsChecked == true; settings.Rivers = RiversCheck.IsChecked == true; settings.PowerLines = PowerCheck.IsChecked == true; settings.Rails = RailsCheck.IsChecked == true; settings.UndergroundRails = MetroCheck.IsChecked == true;
        settings.MapsOutputPath = OutputPathBox.Text.Equals(SettingsStore.DefaultMapsRoot, StringComparison.OrdinalIgnoreCase) ? "" : OutputPathBox.Text.Trim();
        settings.Oasis = OasisCheck.IsChecked == true; settings.Canyons = CanyonCheck.IsChecked == true; settings.Lakes = LakesCheck.IsChecked == true;
        settings.Tier0 = Tier0Slider.Value; settings.Tier1 = Tier1Slider.Value; settings.Tier2 = Tier2Slider.Value;
        settings.BiomeArid = BiomeAridSlider.Value; settings.BiomeTemperate = BiomeTemperateSlider.Value; settings.BiomeTundra = BiomeTundraSlider.Value; settings.BiomeArctic = BiomeArcticSlider.Value; settings.BiomeJungle = BiomeJungleSlider.Value;
        settings.IconSize = IconSizeSlider.Value; settings.ShowRavenIcons = ShowIconsCheck.IsChecked == true; settings.ShowEnvironmentIcons = ShowEnvironmentCheck.IsChecked == true;
        settings.Rules = catalog.ToDictionary(x => x.Id, x => new RuleSetting { State = x.State, Minimum = x.Minimum, Maximum = x.Maximum, UseCustomPrefab = x.UseCustomPrefab }); UpdateSummaries();
    }

    private void UpdateSummaries()
    {
        var special = catalog.Count(x => x.State != RuleState.Optional); var mapName = string.IsNullOrWhiteSpace(MapNameBox.Text) ? "Raven Harita" : MapNameBox.Text.Trim(); DashMapName.Text = mapName; DashSeed.Text = $"Seed {SeedBox.Text} · {SizeBox.Text} metre"; DashRules.Text = $"{special} özel kural"; SidebarProjectName.Text = mapName;
        UpdateFilterCount();
    }

    private void CycleRule_Click(object sender, RoutedEventArgs e) { if (((Button)sender).Tag is MonumentRuleViewModel vm) { vm.Cycle(); MonumentView.Refresh(); UpdateSummaries(); ScheduleSave(); } }
    private void GodRockMinus_Click(object sender, RoutedEventArgs e) { if (((Button)sender).Tag is MonumentRuleViewModel vm && vm.IsGodRock) { vm.TargetCount--; UpdateSummaries(); ScheduleSave(); } }
    private void GodRockPlus_Click(object sender, RoutedEventArgs e) { if (((Button)sender).Tag is MonumentRuleViewModel vm && vm.IsGodRock) { vm.TargetCount++; UpdateSummaries(); ScheduleSave(); } }
    private void TogglePrefab_Click(object sender, RoutedEventArgs e)
    {
        if (((Button)sender).Tag is not MonumentRuleViewModel vm || !vm.CustomPrefabAvailable) return;
        if (vm.State == RuleState.Blocked)
        {
            TopStatus.Text = "Özel prefab kullanılamaz";
            return;
        }
        if (vm.State != RuleState.Required) vm.State = RuleState.Required;
        vm.UseCustomPrefab = !vm.UseCustomPrefab;
        TopStatus.Text = vm.UseCustomPrefab ? $"● {vm.Name}: özel prefab" : $"● {vm.Name}: vanilla";
        MonumentView.Refresh();
        UpdateFilterCount();
        ScheduleSave();
    }

    private void AdvancedValue_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (BiomeSummaryText is null || TierSummaryText is null) return;
        UpdateAdvancedLabels();
        if (IsLoaded) ScheduleSave();
    }

    private void UpdateAdvancedLabels()
    {
        if (BiomeAridValue is null || Tier0Value is null) return;
        static string P(double v) => $"{Math.Round(v):0}%";
        BiomeAridValue.Text = P(BiomeAridSlider.Value);
        BiomeTemperateValue.Text = P(BiomeTemperateSlider.Value);
        BiomeTundraValue.Text = P(BiomeTundraSlider.Value);
        BiomeArcticValue.Text = P(BiomeArcticSlider.Value);
        BiomeJungleValue.Text = P(BiomeJungleSlider.Value);
        Tier0Value.Text = P(Tier0Slider.Value);
        Tier1Value.Text = P(Tier1Slider.Value);
        Tier2Value.Text = P(Tier2Slider.Value);
        var mainBiome = BiomeAridSlider.Value + BiomeTemperateSlider.Value + BiomeTundraSlider.Value + BiomeArcticSlider.Value;
        var tierTotal = Tier0Slider.Value + Tier1Slider.Value + Tier2Slider.Value;

        static double Share(double value, double total) => total > 0.0001 ? value / total * 100d : 0d;
        var aridShare = Share(BiomeAridSlider.Value, mainBiome);
        var temperateShare = Share(BiomeTemperateSlider.Value, mainBiome);
        var tundraShare = Share(BiomeTundraSlider.Value, mainBiome);
        var arcticShare = Share(BiomeArcticSlider.Value, mainBiome);
        var tier0Share = Share(Tier0Slider.Value, tierTotal);
        var tier1Share = Share(Tier1Slider.Value, tierTotal);
        var tier2Share = Share(Tier2Slider.Value, tierTotal);

        BiomeSummaryText.Text =
            $"Çöl %{aridShare:0}  ·  Ilıman %{temperateShare:0}  ·  Tundra %{tundraShare:0}  ·  Kar %{arcticShare:0}  |  Jungle %{BiomeJungleSlider.Value:0} ayrı";
        TierSummaryText.Text =
            $"Tier 0 %{tier0Share:0}  ·  Tier 1 %{tier1Share:0}  ·  Tier 2 %{tier2Share:0}";
    }

    private void ResetBiome_Click(object sender, RoutedEventArgs e)
    {
        BiomeAridSlider.Value = 40;
        BiomeTemperateSlider.Value = 15;
        BiomeTundraSlider.Value = 15;
        BiomeArcticSlider.Value = 30;
        BiomeJungleSlider.Value = 70;
        UpdateAdvancedLabels();
        ScheduleSave();
        TopStatus.Text = "Biyom dağılımı varsayılana döndürüldü";
    }

    private void ResetTier_Click(object sender, RoutedEventArgs e)
    {
        Tier0Slider.Value = 30;
        Tier1Slider.Value = 30;
        Tier2Slider.Value = 40;
        UpdateAdvancedLabels();
        ScheduleSave();
        TopStatus.Text = "Tier dağılımı varsayılana döndürüldü";
    }
    private void ResetRules_Click(object sender, RoutedEventArgs e)
    {
        foreach (var vm in MonumentItems) { vm.State = RuleState.Optional; vm.UseCustomPrefab = false; }
        selectedStateFilter = "Tümü";
        if (StateFilterBox is not null) StateFilterBox.SelectedIndex = 0;
        MonumentView.Refresh();
        UpdateSummaries();
        ScheduleSave();
    }

    private void Search_TextChanged(object sender, TextChangedEventArgs e)
    {
        searchText = ((TextBox)sender).Text.Trim();
        if (sender == QuickSearch && MonumentSearch.Text != searchText) MonumentSearch.Text = searchText;
        else if (sender == MonumentSearch && QuickSearch.Text != searchText) QuickSearch.Text = searchText;
        MonumentView.Refresh();
        UpdateFilterCount();
    }

    private void Category_Changed(object sender, SelectionChangedEventArgs e)
    {
        selectedCategory = Convert.ToString(CategoryBox.SelectedItem) ?? "Tümü";
        MonumentView.Refresh();
        UpdateFilterCount();
    }

    private void StateFilter_Changed(object sender, SelectionChangedEventArgs e)
    {
        selectedStateFilter = Convert.ToString(StateFilterBox.SelectedItem) ?? "Tümü";
        MonumentView.Refresh();
        UpdateFilterCount();
    }

    private bool FilterRule(object item)
    {
        if (item is not MonumentRuleViewModel vm) return false;
        if (selectedCategory != "Tümü" && vm.Category != selectedCategory) return false;
        if (selectedStateFilter == "Olsun" && vm.State != RuleState.Required) return false;
        if (selectedStateFilter == "Olmasın" && vm.State != RuleState.Blocked) return false;
        if (selectedStateFilter == "İsteğe bağlı" && vm.State != RuleState.Optional) return false;
        return searchText.Length == 0 ||
               (vm.Name + " " + vm.Category).Contains(searchText, StringComparison.CurrentCultureIgnoreCase);
    }

    private void UpdateFilterCount()
    {
        if (FilterCountText is null) return;
        FilterCountText.Text = $"{MonumentView.Cast<object>().Count()} / {MonumentItems.Count}";
    }

    private void NewProject_Click(object sender, RoutedEventArgs e)
    {
        var rust = settings.RustServerPath; var recent = settings.RecentProjects.ToList(); var output = settings.MapsOutputPath; settings = new AppSettings { RustServerPath = rust, RecentProjects = recent, MapsOutputPath = output };
        foreach (var vm in MonumentItems) { vm.Rule.Minimum = 0; vm.Rule.Maximum = 99; vm.State = RuleState.Optional; vm.UseCustomPrefab = false; } LoadSettingsIntoUi(); result = null; MapView.Document = null; MarkerDetailsCard.Visibility = Visibility.Collapsed; Navigate("Studio");
    }
    private void OpenProject_Click(object sender, RoutedEventArgs e) { var dialog = new OpenFileDialog { Filter = "Raven harita ayarı|*.ravenmap|JSON ayarı|*.json", InitialDirectory = SettingsStore.ConfigRoot }; if (dialog.ShowDialog(this) == true) LoadProject(dialog.FileName); }
    private void SaveProject_Click(object sender, RoutedEventArgs e) { SaveProject(string.IsNullOrWhiteSpace(settings.CurrentProjectPath) ? SettingsStore.DefaultProjectPath(MapNameBox.Text) : settings.CurrentProjectPath); }
    private void SaveProjectAs_Click(object sender, RoutedEventArgs e) => SaveProjectAs();
    private void SaveProjectAs() { var dialog = new SaveFileDialog { Filter = "Raven harita ayarı|*.ravenmap", InitialDirectory = SettingsStore.ConfigRoot, FileName = SettingsStore.SafeName(MapNameBox.Text, "RavenHarita") + ".ravenmap", AddExtension = true }; if (dialog.ShowDialog(this) == true) SaveProject(dialog.FileName); }
    private void SaveProject(string path)
    {
        try { SyncUiToSettings(); Directory.CreateDirectory(Path.GetDirectoryName(path)!); settings.CurrentProjectPath = path; AddRecent(path); var temp = path + ".tmp"; File.WriteAllText(temp, JsonSerializer.Serialize(settings, jsonOptions)); File.Move(temp, path, true); SettingsStore.Save(settings); LoadSettingsIntoUi(); TopStatus.Text = "Ayarlar kaydedildi"; }
        catch (Exception ex) { MessageBox.Show(this, ex.Message, "Ayarlar kaydedilemedi", MessageBoxButton.OK, MessageBoxImage.Error); }
    }
    private void LoadProject(string path)
    {
        try { var loaded = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(path), jsonOptions) ?? throw new InvalidDataException("Ayar dosyası okunamadı."); loaded.CurrentProjectPath = path; settings = loaded; AddRecent(path); LoadSettingsIntoUi(); SettingsStore.Save(settings); Navigate("Studio"); TopStatus.Text = "Harita ayarları yüklendi"; }
        catch (Exception ex) { MessageBox.Show(this, ex.Message, "Ayarlar yüklenemedi", MessageBoxButton.OK, MessageBoxImage.Error); }
    }
    private void AddRecent(string path) { settings.RecentProjects.RemoveAll(x => x.Equals(path, StringComparison.OrdinalIgnoreCase)); settings.RecentProjects.Insert(0, path); if (settings.RecentProjects.Count > 10) settings.RecentProjects.RemoveRange(10, settings.RecentProjects.Count - 10); }
    private void RecentProject_DoubleClick(object sender, System.Windows.Input.MouseButtonEventArgs e) { if (RecentProjectsList.SelectedItem is RecentProjectEntry project) LoadProject(project.FullPath); }
    private void OpenRecentProject_Click(object sender, RoutedEventArgs e) { if (((Button)sender).Tag is RecentProjectEntry project) LoadProject(project.FullPath); }
    private void StepHero(int direction) { heroIndex = (heroIndex + direction + heroBackgrounds.Length) % heroBackgrounds.Length; UpdateHeroBackground(); }
    private void UpdateHeroBackground()
    {
        DashboardHeroImage.Source = new BitmapImage(new Uri($"pack://application:,,,/Assets/backgrounds/{heroBackgrounds[heroIndex]}", UriKind.Absolute));
    }
    private void ScheduleSave() { saveTimer.Stop(); saveTimer.Start(); }

    private async void Generate_Click(object sender, RoutedEventArgs e) => await GenerateAsync();
    private async Task GenerateAsync()
    {
        SyncUiToSettings(); if (string.IsNullOrWhiteSpace(settings.CurrentProjectPath)) SaveProject(SettingsStore.DefaultProjectPath(settings.MapName)); SettingsStore.Save(settings); cancellation = new(); GenerateButton.IsEnabled = false; CancelButton.IsEnabled = true; ExportPngButton.IsEnabled = ExportMapButton.IsEnabled = false; GenerationProgress.IsIndeterminate = false; GenerationProgress.Minimum = 0; GenerationProgress.Maximum = 100; GenerationProgress.Value = 0; SideProgressTitle.Text = "Üretim kuyruğu hazırlanıyor"; MapCanvasStatus.Text = "Üretim devam ediyor"; TopStatus.Text = "Üretim sürüyor"; StudioLog.Clear(); FullLog.Clear(); Navigate("Studio");
        var queueSettings = JsonSerializer.Deserialize<AppSettings>(JsonSerializer.Serialize(settings, jsonOptions), jsonOptions)!;
        var count = Math.Clamp(queueSettings.GenerationCount, 1, 7);
        var seeds = BuildGenerationSeeds(queueSettings.Seed, count);
        var completed = 0;
        try
        {
            for (var index = 0; index < seeds.Count; index++)
            {
                cancellation.Token.ThrowIfCancellationRequested();
                queueSettings.Seed = seeds[index];
                var number = index + 1;
                SideProgressTitle.Text = $"Harita {number}/{count} üretiliyor";
                SideProgressDetail.Text = $"Seed {queueSettings.Seed} · {queueSettings.WorldSize} metre";
                TopStatus.Text = $"Kuyruk {number}/{count}";
                GenerationProgress.Value = index * 100d / count;
                AppendLog($"KUYRUK: Harita {number}/{count} başlatıldı — Seed {queueSettings.Seed}");

                var service = new GenerationService(catalog);
                service.Log += message => Dispatcher.Invoke(() => AppendLog(message));
                result = await service.GenerateAsync(queueSettings, cancellation.Token);
                LoadMap(result.RawImagePath, result.ReportFolder, result.WorldSize, result.Seed);
                RecordGenerationHistory(result);
                completed++;
                GenerationProgress.Value = completed * 100d / count;
                AppendLog($"KUYRUK: Harita {number}/{count} tamamlandı — {result.OutputFolder}");
            }

            settings.Seed = seeds[^1];
            SeedBox.Text = settings.Seed.ToString();
            SettingsStore.Save(settings);
            SideProgressTitle.Text = count == 1 ? "Üretim tamamlandı" : $"{completed} haritanın tamamı üretildi";
            SideProgressDetail.Text = result is null ? "" : $"Son harita: {result.OutputFolder}";
            MapCanvasStatus.Text = $"{MapView.Document?.Markers.Count ?? 0} ikon · otomatik kaydedildi";
            TopStatus.Text = count == 1 ? "Harita hazır" : "Kuyruk tamamlandı";
            GenerationProgress.Value = 100;
            ExportPngButton.IsEnabled = ExportMapButton.IsEnabled = true;
        }
        catch (OperationCanceledException) { SideProgressTitle.Text = $"Kuyruk iptal edildi ({completed}/{count} tamamlandı)"; SideProgressDetail.Text = "Tamamlanan haritalar korunmuştur."; TopStatus.Text = "İptal edildi"; }
        catch (Exception ex) { AppendLog("HATA: " + ex.Message); SideProgressTitle.Text = $"Kuyruk durdu ({completed}/{count} tamamlandı)"; SideProgressDetail.Text = ex.Message; TopStatus.Text = "Hata"; MessageBox.Show(this, $"{completed}/{count} harita tamamlandı.\n\n{ex.Message}", "Üretim kuyruğu hatası", MessageBoxButton.OK, MessageBoxImage.Error); }
        finally { GenerationProgress.IsIndeterminate = false; GenerateButton.IsEnabled = true; CancelButton.IsEnabled = false; cancellation?.Dispose(); cancellation = null; }
    }

    private static List<int> BuildGenerationSeeds(int firstSeed, int count)
    {
        var seeds = new List<int>(count) { firstSeed };
        var used = new HashSet<int> { firstSeed };
        while (seeds.Count < count)
        {
            var seed = Random.Shared.Next(1, int.MaxValue);
            if (used.Add(seed)) seeds.Add(seed);
        }
        return seeds;
    }

    private void LoadMap(string image, string reports, int size, int seed)
    {
        var doc = MapDataService.Load(image, reports, size, seed, catalog); MapView.Document = doc; MarkerDetailsCard.Visibility = Visibility.Collapsed; MapCanvasStatus.Text = $"{doc.Markers.Count} ikon · {size} metre"; result ??= new("", "", Path.GetDirectoryName(image) ?? "", doc.Markers.Count, doc.Markers.Count(x => x.Category == "god_rocks"), image, reports, size, seed); ExportPngButton.IsEnabled = true; if (!string.IsNullOrWhiteSpace(result.MapPath) && File.Exists(result.MapPath)) ExportMapButton.IsEnabled = true; ValidateMap();
    }
    private void LoadLatestMap_Click(object sender, RoutedEventArgs e)
    {
        SyncUiToSettings(); var image = Path.Combine(settings.RustServerPath, "mapimages", $"CustomGenerator{settings.WorldSize}_{settings.Seed}.png"); var reports = Path.Combine(settings.RustServerPath, "HarmonyConfig", "RavenMapReports"); var map = Path.Combine(settings.RustServerPath, "maps", $"CustomGenerator{settings.WorldSize}_{settings.Seed}.map");
        if (!File.Exists(image)) { MessageBox.Show(this, "Seçili seed ve boyut için üretilmiş harita bulunamadı.", "Harita bulunamadı"); return; } result = new(map, "", Path.GetDirectoryName(image)!, 0, 0, image, reports, settings.WorldSize, settings.Seed); LoadMap(image, reports, settings.WorldSize, settings.Seed); Navigate("Studio");
    }

    private void ExportPng_Click(object sender, RoutedEventArgs e) { var dialog = new SaveFileDialog { Filter = "PNG haritası|*.png", FileName = settings.MapName + ".png" }; if (dialog.ShowDialog(this) == true) { MapView.Export(dialog.FileName); TopStatus.Text = "PNG kaydedildi"; } }
    private void ExportMap_Click(object sender, RoutedEventArgs e) { if (result is null || !File.Exists(result.MapPath)) return; var dialog = new SaveFileDialog { Filter = "Rust haritası|*.map", FileName = settings.MapName + ".map" }; if (dialog.ShowDialog(this) == true) { File.Copy(result.MapPath, dialog.FileName, true); TopStatus.Text = "MAP kaydedildi"; } }
    private void BrowseRust_Click(object sender, RoutedEventArgs e) { using var dialog = new System.Windows.Forms.FolderBrowserDialog { Description = "RustDedicated.exe bulunan klasörü seçin", SelectedPath = RustPathBox.Text }; if (dialog.ShowDialog() == System.Windows.Forms.DialogResult.OK) { RustPathBox.Text = dialog.SelectedPath; ScheduleSave(); } }
    private void BrowseOutput_Click(object sender, RoutedEventArgs e) { using var dialog = new System.Windows.Forms.FolderBrowserDialog { Description = "MAP ve PNG klasörlerinin kaydedileceği ana klasörü seçin", SelectedPath = OutputPathBox.Text }; if (dialog.ShowDialog() == System.Windows.Forms.DialogResult.OK) { OutputPathBox.Text = dialog.SelectedPath; settings.MapsOutputPath = dialog.SelectedPath; SettingsStore.Save(settings); } }
    private void ResetOutput_Click(object sender, RoutedEventArgs e) { settings.MapsOutputPath = ""; OutputPathBox.Text = SettingsStore.DefaultMapsRoot; SettingsStore.Save(settings); }
    private void OpenConfig_Click(object sender, RoutedEventArgs e) => OpenFolder(SettingsStore.ConfigRoot);
    private void OpenMaps_Click(object sender, RoutedEventArgs e) => OpenFolder(SettingsStore.OutputRoot(settings));
    private void OpenCurrentProjectFolder_Click(object sender, RoutedEventArgs e)
    {
        var projectPath = settings.CurrentProjectPath;
        if (string.IsNullOrWhiteSpace(projectPath))
        {
            MessageBox.Show(this, "Bu harita henüz bir ayar dosyasına kaydedilmedi.", "Ayar dosyası");
            return;
        }

        var folder = Path.GetDirectoryName(projectPath);
        if (string.IsNullOrWhiteSpace(folder))
        {
            MessageBox.Show(this, "Ayar dosyasının klasörü bulunamadı.", "Ayar dosyası");
            return;
        }

        OpenFolder(folder);
    }
    private static void OpenFolder(string path) { Directory.CreateDirectory(path); Process.Start(new ProcessStartInfo("explorer.exe", $"\"{path}\"") { UseShellExecute = true }); }

    private void OnMapMarkerSelected(MapMarker? marker)
    {
        selectedMapMarker = marker;
        if (marker is null)
        {
            MarkerDetailsCard.Visibility = Visibility.Collapsed;
            return;
        }

        var rule = catalog.FirstOrDefault(x => x.Id.Equals(marker.Category, StringComparison.OrdinalIgnoreCase));
        selectedGalleryRule = rule;
        markerGallery = MonumentGalleryService.Load(rule, marker.IconFile);
        markerGalleryIndex = 0;
        MarkerNameText.Text = marker.Name;
        MarkerInfoText.Text = $"{rule?.Category ?? "Harita öğesi"}  ·  X {marker.X:0}  Z {marker.Z:0}";
        MarkerDescriptionText.Text = MonumentGalleryService.DescriptionFor(rule);
        MarkerRuleButton.Visibility = rule is null ? Visibility.Collapsed : Visibility.Visible;
        MarkerGalleryFolderButton.Visibility = rule is null ? Visibility.Collapsed : Visibility.Visible;
        UpdateMarkerGallery();
        MarkerDetailsCard.Visibility = Visibility.Visible;
    }

    private void UpdateMarkerGallery()
    {
        if (markerGallery.Count == 0) return;
        markerGalleryIndex = Math.Clamp(markerGalleryIndex, 0, markerGallery.Count - 1);
        var item = markerGallery[markerGalleryIndex];
        MarkerGalleryImage.Source = item.Source;
        MarkerGalleryImage.Stretch = item.IsFallback ? Stretch.Uniform : Stretch.UniformToFill;
        MarkerFallbackBadge.Visibility = item.IsFallback ? Visibility.Visible : Visibility.Collapsed;
        MarkerGalleryCounter.Text = $"{markerGalleryIndex + 1} / {markerGallery.Count}";
        MarkerGalleryCaption.Text = item.Caption;
        var canNavigate = markerGallery.Count > 1;
        MarkerGalleryPrevious.Visibility = canNavigate ? Visibility.Visible : Visibility.Collapsed;
        MarkerGalleryNext.Visibility = canNavigate ? Visibility.Visible : Visibility.Collapsed;
    }

    private void MarkerGalleryPrevious_Click(object sender, RoutedEventArgs e)
    {
        if (markerGallery.Count < 2) return;
        markerGalleryIndex = (markerGalleryIndex - 1 + markerGallery.Count) % markerGallery.Count;
        UpdateMarkerGallery();
    }

    private void MarkerGalleryNext_Click(object sender, RoutedEventArgs e)
    {
        if (markerGallery.Count < 2) return;
        markerGalleryIndex = (markerGalleryIndex + 1) % markerGallery.Count;
        UpdateMarkerGallery();
    }

    private void OpenMarkerGallery_Click(object sender, RoutedEventArgs e)
    {
        if (selectedGalleryRule is null) return;
        OpenFolder(MonumentGalleryService.FolderFor(selectedGalleryRule.Id));
    }

    private void CloseMarkerDetails_Click(object sender, RoutedEventArgs e) => MapView.ClearSelection();

    private void MarkerRule_Click(object sender, RoutedEventArgs e)
    {
        if (selectedMapMarker is null) return;
        var vm = MonumentItems.FirstOrDefault(x =>
            x.Id.Equals(selectedMapMarker.Category, StringComparison.OrdinalIgnoreCase));
        if (vm is null) return;

        searchText = "";
        selectedCategory = "Tümü";
        selectedStateFilter = "Tümü";
        MonumentSearch.Text = "";
        QuickSearch.Text = "";
        CategoryBox.SelectedIndex = 0;
        StateFilterBox.SelectedIndex = 0;
        MonumentView.Refresh();
        UpdateFilterCount();

        Navigate("Monuments");
        FullMonumentList.SelectedItem = vm;
        FullMonumentList.ScrollIntoView(vm);
    }

    private void RecordGenerationHistory(GenerationResult generated)
    {
        if (MapView.Document is null) return;

        GenerationHistoryStore.Add(new GenerationHistoryEntry
        {
            MapName = settings.MapName,
            Seed = generated.Seed,
            WorldSize = generated.WorldSize,
            GeneratedAt = DateTime.Now,
            IconCount = MapView.Document.Markers.Count,
            GodRockCount = MapView.Document.Markers.Count(x => x.Category == "god_rocks"),
            ValidationIssues = CountValidationIssues(MapView.Document),
            MapPath = generated.MapPath,
            ImagePath = generated.ImagePath,
            RawImagePath = generated.RawImagePath,
            ReportFolder = generated.ReportFolder,
            OutputFolder = generated.OutputFolder,
            ProjectPath = settings.CurrentProjectPath,
            BackupPath = generated.BackupPath
        });
        RefreshHistory();
    }

    private int CountValidationIssues(MapDocument doc)
    {
        var failed = 0;
        foreach (var rule in catalog.Where(x => x.State != RuleState.Optional))
        {
            var count = doc.Markers.Count(x => x.Category == rule.Id);
            var required = Math.Max(1, rule.Minimum);
            var exactGodRock = rule.Id.Equals("god_rocks", StringComparison.OrdinalIgnoreCase);
            var ok = rule.State == RuleState.Required
                ? (exactGodRock ? count == required : count >= required)
                : count == 0;
            if (!ok) failed++;
        }
        return failed;
    }

    private void RefreshHistory_Click(object sender, RoutedEventArgs e) => RefreshHistory();

    private void RefreshHistory()
    {
        if (HistoryList is null) return;

        var previousA = (CompareABox.SelectedItem as GenerationHistoryEntry)?.Id;
        var previousB = (CompareBBox.SelectedItem as GenerationHistoryEntry)?.Id;
        var items = GenerationHistoryStore.Load()
            .OrderByDescending(x => x.GeneratedAt)
            .ToList();

        GenerationHistoryItems.Clear();
        foreach (var item in items) GenerationHistoryItems.Add(item);
        HistoryCountText.Text = $"{GenerationHistoryItems.Count} kayıt";

        CompareABox.SelectedItem = GenerationHistoryItems.FirstOrDefault(x => x.Id == previousA);
        CompareBBox.SelectedItem = GenerationHistoryItems.FirstOrDefault(x => x.Id == previousB);

        if (CompareABox.SelectedItem is null && GenerationHistoryItems.Count > 1)
            CompareABox.SelectedIndex = 1;
        else if (CompareABox.SelectedItem is null && GenerationHistoryItems.Count == 1)
            CompareABox.SelectedIndex = 0;

        if (CompareBBox.SelectedItem is null && GenerationHistoryItems.Count > 0)
            CompareBBox.SelectedIndex = 0;

        if (GenerationHistoryItems.Count > 1 &&
            CompareABox.SelectedItem is GenerationHistoryEntry selectedA &&
            CompareBBox.SelectedItem is GenerationHistoryEntry selectedB &&
            selectedA.Id == selectedB.Id)
        {
            CompareABox.SelectedIndex = 1;
            CompareBBox.SelectedIndex = 0;
        }

        if (GenerationHistoryItems.Count == 0)
        {
            CompareSummaryText.Text = "Henüz üretim geçmişi yok.";
            ComparisonItems.Clear();
        }
    }

    private void LoadHistory_Click(object sender, RoutedEventArgs e)
    {
        if (((Button)sender).Tag is not GenerationHistoryEntry entry) return;
        if (!File.Exists(entry.RawImagePath) ||
            !File.Exists(Path.Combine(entry.ReportFolder, $"RavenMapReport_{entry.WorldSize}_{entry.Seed}.json")))
        {
            MessageBox.Show(this,
                "Bu geçmiş kaydının önizleme dosyaları bulunamadı.",
                "Harita geçmişi",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
            return;
        }

        result = new GenerationResult(entry.MapPath, entry.ImagePath, entry.OutputFolder,
            entry.IconCount, entry.GodRockCount, entry.RawImagePath, entry.ReportFolder,
            entry.WorldSize, entry.Seed, entry.BackupPath);
        LoadMap(entry.RawImagePath, entry.ReportFolder, entry.WorldSize, entry.Seed);
        Navigate("Studio");
        TopStatus.Text = "Geçmiş harita açıldı";
    }

    private void OpenHistoryFolder_Click(object sender, RoutedEventArgs e)
    {
        if (((Button)sender).Tag is not GenerationHistoryEntry entry) return;
        if (!Directory.Exists(entry.OutputFolder))
        {
            MessageBox.Show(this, "Harita klasörü artık mevcut değil.", "Harita geçmişi");
            return;
        }
        OpenFolder(entry.OutputFolder);
    }

    private void OpenHistoryBackup_Click(object sender, RoutedEventArgs e)
    {
        if (((Button)sender).Tag is not GenerationHistoryEntry entry ||
            string.IsNullOrWhiteSpace(entry.BackupPath) ||
            !Directory.Exists(entry.BackupPath))
            return;
        OpenFolder(entry.BackupPath);
    }

    private void CompareHistory_Click(object sender, RoutedEventArgs e)
    {
        if (CompareABox.SelectedItem is not GenerationHistoryEntry a ||
            CompareBBox.SelectedItem is not GenerationHistoryEntry b)
        {
            CompareSummaryText.Text = "Karşılaştırmak için iki üretim seçin.";
            return;
        }

        if (a.Id == b.Id)
        {
            CompareSummaryText.Text = "Karşılaştırma için iki farklı üretim seçin.";
            ComparisonItems.Clear();
            return;
        }

        try
        {
            var docA = MapDataService.Load(a.RawImagePath, a.ReportFolder, a.WorldSize, a.Seed, catalog);
            var docB = MapDataService.Load(b.RawImagePath, b.ReportFolder, b.WorldSize, b.Seed, catalog);
            var countsA = docA.Markers.GroupBy(x => x.Category)
                .ToDictionary(x => x.Key, x => x.Count(), StringComparer.OrdinalIgnoreCase);
            var countsB = docB.Markers.GroupBy(x => x.Category)
                .ToDictionary(x => x.Key, x => x.Count(), StringComparer.OrdinalIgnoreCase);

            var ids = countsA.Keys.Concat(countsB.Keys)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            var rows = ids.Select(id =>
            {
                var rule = catalog.FirstOrDefault(x => x.Id.Equals(id, StringComparison.OrdinalIgnoreCase));
                return new MapComparisonRow
                {
                    Name = rule?.Name ?? id,
                    CountA = countsA.GetValueOrDefault(id),
                    CountB = countsB.GetValueOrDefault(id)
                };
            })
            .OrderByDescending(x => Math.Abs(x.Difference))
            .ThenBy(x => x.Name)
            .ToList();

            ComparisonItems.Clear();
            foreach (var row in rows) ComparisonItems.Add(row);

            var changed = rows.Count(x => x.Difference != 0);
            CompareSummaryText.Text =
                $"{changed} öğede fark · A: {docA.Markers.Count} ikon · B: {docB.Markers.Count} ikon";
        }
        catch (Exception ex)
        {
            ComparisonItems.Clear();
            CompareSummaryText.Text = "Karşılaştırma yapılamadı.";
            MessageBox.Show(this, ex.Message, "Harita karşılaştırma",
                MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void AddMonument_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new AddMonumentWindow(catalog.Select(x => x.Category)) { Owner = this };
        if (dialog.ShowDialog() != true || dialog.CreatedRule is null) return;
        var rule = dialog.CreatedRule; catalog.Add(rule); var vm = new MonumentRuleViewModel(rule); MonumentItems.Add(vm); IconCache.Remove(rule.Icon);
        if (!CategoryBox.Items.Cast<object>().Any(x => string.Equals(Convert.ToString(x), rule.Category, StringComparison.CurrentCultureIgnoreCase))) CategoryBox.Items.Add(rule.Category);
        MonumentView.Refresh(); settings.Rules[rule.Id] = new RuleSetting { UseCustomPrefab = false }; SettingsStore.Save(settings); TopStatus.Text = "Monument eklendi";
    }
    private void RandomSeed_Click(object sender, RoutedEventArgs e) { SeedBox.Text = Random.Shared.Next(1, int.MaxValue).ToString(); ScheduleSave(); }
    private void Cancel_Click(object sender, RoutedEventArgs e) => cancellation?.Cancel();
    private void ZoomIn_Click(object sender, RoutedEventArgs e) => MapView.ZoomIn(); private void ZoomOut_Click(object sender, RoutedEventArgs e) => MapView.ZoomOut(); private void FitMap_Click(object sender, RoutedEventArgs e) => MapView.Fit();
    private void IconSize_Changed(object sender, RoutedPropertyChangedEventArgs<double> e) { if (IconSizeLabel is null || MapView is null) return; IconSizeLabel.Text = $"{e.NewValue:0} px"; MapView.IconSize = e.NewValue; MapView.InvalidateVisual(); if (IsLoaded) ScheduleSave(); }
    private void Layer_Changed(object sender, RoutedEventArgs e) { if (MapView is null || ShowIconsCheck is null || ShowEnvironmentCheck is null) return; MapView.ShowIcons = ShowIconsCheck.IsChecked == true; MapView.ShowEnvironment = ShowEnvironmentCheck.IsChecked == true; MapView.InvalidateVisual(); if (IsLoaded) ScheduleSave(); }
    private void ClearLog_Click(object sender, RoutedEventArgs e) { StudioLog.Clear(); FullLog.Clear(); }
    private void AppendLog(string message) { var line = $"[{DateTime.Now:HH:mm:ss}]  {message}{Environment.NewLine}"; StudioLog.AppendText(line); FullLog.AppendText(line); StudioLog.ScrollToEnd(); FullLog.ScrollToEnd(); }
    private void Validate_Click(object sender, RoutedEventArgs e) => ValidateMap();
    private void ValidateMap()
    {
        var flow = ValidationText.Document;
        flow.Blocks.Clear();
        flow.PagePadding = new Thickness(0);

        var normal = (System.Windows.Media.Brush)FindResource("TextBrush");
        var muted = (System.Windows.Media.Brush)FindResource("MutedBrush");
        var success = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x6F, 0xDB, 0xAE));
        var error = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0xFF, 0x68, 0x68));

        void AddLine(string text, System.Windows.Media.Brush brush, FontWeight? weight = null)
        {
            var paragraph = new Paragraph
            {
                Margin = new Thickness(0),
                Padding = new Thickness(0),
                FontFamily = new System.Windows.Media.FontFamily("Segoe UI Variable Text")
            };
            paragraph.Inlines.Add(new Run(text)
            {
                Foreground = brush,
                FontWeight = weight ?? FontWeights.Normal
            });
            flow.Blocks.Add(paragraph);
        }

        if (MapView.Document is null)
        {
            AddLine("Henüz doğrulanacak harita yüklenmedi.", muted);
            return;
        }

        var doc = MapView.Document;
        var activeRules = catalog.Where(x => x.State != RuleState.Optional).ToList();
        var results = activeRules.Select(rule =>
        {
            var count = doc.Markers.Count(x => x.Category == rule.Id);
            var required = Math.Max(1, rule.Minimum);
            var exactGodRock = rule.Id.Equals("god_rocks", StringComparison.OrdinalIgnoreCase);
            var ok = rule.State == RuleState.Required
                ? (exactGodRock ? count == required : count >= required)
                : count == 0;
            var mode = rule.State == RuleState.Required
                ? (exactGodRock ? $"tam {required}" : "doğal / tek seed")
                : "yasaklı";
            return (rule, count, ok, mode);
        }).ToList();

        var failedCount = results.Count(x => !x.ok);
        ValidationTotalText.Text = results.Count.ToString();
        ValidationSuccessText.Text = (results.Count - failedCount).ToString();
        ValidationErrorText.Text = failedCount.ToString();

        AddLine($"Harita: {settings.MapName}", normal, FontWeights.SemiBold);
        AddLine($"Boyut / Seed: {doc.WorldSize} / {settings.Seed}", normal);
        AddLine($"Raporlanmış ikon: {doc.Markers.Count}", normal);
        AddLine("God Rock hedef adedi kesin; diğer monumentler doğal üretime göre doğrulanır.", muted);
        AddLine("", normal);

        if (failedCount == 0)
            AddLine("Tüm seçili kurallar karşılandı.", success, FontWeights.Bold);
        else
            AddLine($"{failedCount} kural karşılanmadı.", error, FontWeights.Bold);

        AddLine("", normal);

        foreach (var item in results)
        {
            var paragraph = new Paragraph
            {
                Margin = new Thickness(0),
                Padding = new Thickness(0),
                FontFamily = new System.Windows.Media.FontFamily("Segoe UI Variable Text")
            };
            var statusBrush = item.ok ? success : error;
            paragraph.Inlines.Add(new Run(item.ok ? "✓" : "×")
            {
                Foreground = statusBrush,
                FontWeight = FontWeights.Bold
            });
            paragraph.Inlines.Add(new Run($"  {item.rule.Name,-28} {item.count,3}  ({item.mode})")
            {
                // Kullanıcının istediği görünürlük: false/eksik satırın tamamı kırmızı.
                Foreground = item.ok ? normal : error,
                FontWeight = item.ok ? FontWeights.Normal : FontWeights.SemiBold
            });
            flow.Blocks.Add(paragraph);
        }

        if (activeRules.Count == 0)
            AddLine("Özel monument kuralı seçilmedi.", muted);
    }

}

public sealed record RecentProjectEntry(string Name, string SavedAt, string Location, string FullPath);
