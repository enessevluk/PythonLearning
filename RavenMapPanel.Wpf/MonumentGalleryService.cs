using System.Windows.Media.Imaging;

namespace RavenMapPanel;

internal sealed record MonumentGalleryImage(BitmapImage Source, string Caption, bool IsFallback = false);

internal static class MonumentGalleryService
{
    private static readonly string[] Extensions = [".png", ".jpg", ".jpeg", ".webp", ".bmp"];
    private static readonly Dictionary<string, string> Descriptions = new(StringComparer.OrdinalIgnoreCase)
    {
        ["airfield"] = "Geniş pistleri, hangarları, yer altı bağlantıları ve açık görüş hatları olan büyük bir endüstriyel monument.",
        ["launch_site"] = "Fırlatma yapıları ve çok katlı endüstriyel alanlarıyla geniş, yüksek riskli bir monument.",
        ["military_tunnels"] = "Dar koridorlar ve yer altı bölümleriyle yakın mesafe çatışmalarına uygun askeri kompleks.",
        ["trainyard"] = "Raylar, depolar ve yüksek yapılar çevresinde şekillenen büyük bir endüstri sahası.",
        ["powerplant"] = "Büyük tesis binaları, boru hatları ve çok seviyeli geçişlerden oluşan enerji kompleksi.",
        ["water_treatment_plant"] = "Su havuzları, arıtma binaları ve açık endüstriyel alanlardan oluşan geniş tesis.",
        ["junkyard"] = "Hurda yığınları, araç parçaları ve dar geçişleri olan açık alan monumenti.",
        ["harbor_1"] = "Kıyı yapıları, depolar ve yükleme alanları bulunan liman kompleksi.",
        ["harbor_2"] = "Kıyı yapıları, depolar ve yükleme alanları bulunan liman kompleksi.",
        ["outpost"] = "Ticaret ve servis noktaları barındıran korumalı güvenli bölge.",
        ["bandit_town"] = "Ticaret noktaları ve oyun alanları bulunan güvenli bölge monumenti.",
        ["fishing_village"] = "Kıyıda tekne ve deniz ekipmanı hizmetleri sunan küçük güvenli yerleşim.",
        ["oilrig_1"] = "Deniz üzerinde çok katlı platform; dar merdivenler ve açık güverteler içerir.",
        ["oilrig_2"] = "Deniz üzerinde geniş ve çok katlı petrol platformu.",
        ["lighthouse"] = "Kıyıda bulunan, dikey yapısı ve çevresindeki küçük binalarıyla kolay tanınan monument.",
        ["sphere_tank"] = "Küresel ana yapısı ve çevresindeki endüstriyel binalarla dikey oynanış sunan alan.",
        ["satellite_dish"] = "Uydu çanakları ve çevre binalarından oluşan açık görüşlü tesis.",
        ["sewer_branch"] = "Yüzey yapıları ile yer altı tünellerini birleştiren orta boy monument.",
        ["arctic_research_base"] = "Karlı biyomda modüler binalar ve açık geçişlerden oluşan araştırma üssü.",
        ["nuclear_missile_silo"] = "Derin yer altı katmanları ve kontrollü geçişleri bulunan askeri silo kompleksi."
    };

    public static string FolderFor(string monumentId) =>
        Path.Combine(SettingsStore.MonumentGalleryRoot, SettingsStore.SafeName(monumentId, "monument"));

    public static string DescriptionFor(MonumentRule? rule) => rule is null
        ? "Harita üzerinde algılanan çevre veya altyapı öğesi."
        : Descriptions.GetValueOrDefault(rule.Id, $"{rule.Category} grubunda yer alan {rule.Name} monumenti.");

    public static List<MonumentGalleryImage> Load(MonumentRule? rule, string iconFile)
    {
        var result = new List<MonumentGalleryImage>();
        if (rule is not null)
        {
            var folder = FolderFor(rule.Id);
            Directory.CreateDirectory(folder);
            foreach (var file in Directory.EnumerateFiles(folder)
                         .Where(x => Extensions.Contains(Path.GetExtension(x), StringComparer.OrdinalIgnoreCase))
                         .OrderBy(Path.GetFileName, StringComparer.CurrentCultureIgnoreCase))
            {
                var image = LoadFile(file);
                if (image is not null) result.Add(new(image, Path.GetFileNameWithoutExtension(file)));
            }
        }

        if (result.Count == 0)
        {
            var source = new BitmapImage(new Uri(AssetStore.IconSource(iconFile), UriKind.RelativeOrAbsolute));
            result.Add(new(source, "Bu monument için galeri görseli henüz eklenmemiş.", true));
        }
        return result;
    }

    private static BitmapImage? LoadFile(string path)
    {
        try
        {
            using var stream = File.OpenRead(path);
            var image = new BitmapImage();
            image.BeginInit();
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.DecodePixelWidth = 1200;
            image.StreamSource = stream;
            image.EndInit();
            image.Freeze();
            return image;
        }
        catch { return null; }
    }
}
