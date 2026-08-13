using Microsoft.Win32;
using System.Globalization;
using System.Text;
using System.Windows;
using System.Windows.Media.Imaging;
using OpenFileDialog = Microsoft.Win32.OpenFileDialog;
using MessageBox = System.Windows.MessageBox;

namespace RavenMapPanel;

public partial class AddMonumentWindow : Window
{
    private string iconPath = "";
    public MonumentRule? CreatedRule { get; private set; }

    public AddMonumentWindow(IEnumerable<string> categories)
    {
        InitializeComponent();
        foreach (var category in categories.Distinct().OrderBy(x => x)) CategoryBox.Items.Add(category);
        CategoryBox.Items.Add("Özel Monumentler");
        CategoryBox.Text = "Özel Monumentler";
    }

    private void ChooseIcon_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog { Filter = "PNG ikonu|*.png", Title = "Monument ikonunu seçin" };
        if (dialog.ShowDialog(this) != true) return;
        try
        {
            using var bitmap = new System.Drawing.Bitmap(dialog.FileName);
            iconPath = dialog.FileName;
            IconInfoText.Text = $"{Path.GetFileName(iconPath)}  ·  {bitmap.Width} × {bitmap.Height} px";
            var preview = new BitmapImage(); preview.BeginInit(); preview.CacheOption = BitmapCacheOption.OnLoad; preview.UriSource = new Uri(iconPath); preview.EndInit(); preview.Freeze(); IconPreview.Source = preview;
        }
        catch (Exception ex) { MessageBox.Show(this, ex.Message, "PNG okunamadı", MessageBoxButton.OK, MessageBoxImage.Warning); }
    }

    private void Add_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var name = DisplayNameBox.Text.Trim();
            var prefab = PrefabBox.Text.Trim().Replace('\\', '/');
            var category = CategoryBox.Text.Trim();
            if (name.Length < 2) throw new InvalidDataException("Haritada gösterilecek ismi yazın.");
            if (prefab.Length < 4) throw new InvalidDataException("Rust prefab adını veya tam prefab yolunu yazın.");
            if (category.Length < 2) category = "Özel Monumentler";
            if (string.IsNullOrWhiteSpace(iconPath)) throw new InvalidDataException("Monument için bir PNG seçin.");
            var id = "custom_" + Slug(name);
            var imported = AssetStore.ImportCustomIcon(iconPath, id);
            CreatedRule = new MonumentRule { Id = id, Name = name, Category = category, Icon = imported.FileName,
                Prefabs = [prefab], Aliases = [name], RenderOnMap = true, IsCustom = true, Maximum = 99 };
            AssetStore.SaveCustomRule(CreatedRule);
            DialogResult = true;
        }
        catch (Exception ex) { MessageBox.Show(this, ex.Message, "Monument eklenemedi", MessageBoxButton.OK, MessageBoxImage.Warning); }
    }

    private static string Slug(string value)
    {
        var normalized = value.Normalize(NormalizationForm.FormD);
        var output = new StringBuilder();
        foreach (var c in normalized)
        {
            if (CharUnicodeInfo.GetUnicodeCategory(c) == UnicodeCategory.NonSpacingMark) continue;
            if (char.IsLetterOrDigit(c)) output.Append(char.ToLowerInvariant(c));
            else if (output.Length > 0 && output[^1] != '_') output.Append('_');
        }
        return output.ToString().Trim('_');
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;
}
