using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Point = System.Windows.Point;
using Vector = System.Windows.Vector;
using MouseEventArgs = System.Windows.Input.MouseEventArgs;
using Color = System.Windows.Media.Color;
using Pen = System.Windows.Media.Pen;
using Brushes = System.Windows.Media.Brushes;
using Cursors = System.Windows.Input.Cursors;
using FlowDirection = System.Windows.FlowDirection;

namespace RavenMapPanel;

public sealed class MapViewport : FrameworkElement
{
    private MapDocument? document;
    private double zoom = 1;
    private Vector pan;
    private Point dragStart;
    private Vector panStart;
    private bool dragging;
    private bool dragMoved;
    private Point mousePoint;
    private MapMarker? selectedMarker;
    public double IconSize { get; set; } = 42;
    public bool ShowIcons { get; set; } = true;
    public bool ShowEnvironment { get; set; } = true;
    public double ZoomPercent => zoom * 100;
    public MapDocument? Document
    {
        get => document;
        set
        {
            document = value;
            selectedMarker = null;
            Fit();
            MarkerSelected?.Invoke(null);
        }
    }
    public MapMarker? SelectedMarker => selectedMarker;
    public event Action<double>? ZoomChanged;
    public event Action<MapMarker?>? MarkerSelected;

    public MapViewport()
    {
        Focusable = true; ClipToBounds = true; SnapsToDevicePixels = true;
        MouseWheel += Wheel; MouseLeftButtonDown += Down; MouseLeftButtonUp += Up; MouseMove += Move; MouseLeave += (_, _) => { if (dragging) ReleaseMouseCapture(); dragging = false; ToolTip = null; };
    }

    public void Fit() { zoom = 1; pan = default; InvalidateVisual(); ZoomChanged?.Invoke(100); }
    public void ClearSelection()
    {
        selectedMarker = null;
        MarkerSelected?.Invoke(null);
        InvalidateVisual();
    }
    public void ZoomIn() => SetZoom(zoom * 1.18, new Point(ActualWidth / 2, ActualHeight / 2));
    public void ZoomOut() => SetZoom(zoom / 1.18, new Point(ActualWidth / 2, ActualHeight / 2));

    protected override void OnRender(DrawingContext dc)
    {
        dc.DrawRectangle(new SolidColorBrush(Color.FromRgb(6, 17, 23)), null, new Rect(RenderSize));
        DrawGrid(dc);
        if (document?.Image is null) { DrawEmpty(dc); return; }
        var (scale, origin) = Transform();
        var imageRect = new Rect(origin.X, origin.Y, document.ImageWidth * scale, document.ImageHeight * scale);
        dc.PushClip(new RectangleGeometry(new Rect(RenderSize))); dc.DrawImage(document.Image, imageRect);
        if (ShowIcons)
        {
            foreach (var marker in document.Markers)
            {
                if (!ShowEnvironment && IsEnvironment(marker.Category)) continue;
                var p = document.Project(marker.X, marker.Z); var screen = new Point(origin.X + p.X * scale, origin.Y + p.Y * scale);
                // IconSize is expressed in source-map pixels. Keeping it in the same
                // coordinate space as the map prevents oversized, apparently offset
                // markers when a 3400px map is fitted into a smaller viewport.
                var side = DisplayIconSide(marker.Category, scale); var icon = IconCache.Get(marker.IconFile);
                dc.DrawImage(icon, IconRect(icon, screen, side));
                if (ReferenceEquals(marker, selectedMarker))
                {
                    var ring = Math.Max(11, side * .72);
                    dc.DrawEllipse(null,
                        new Pen(new SolidColorBrush(Color.FromRgb(0xE6, 0x5B, 0x35)), 2.2),
                        screen, ring, ring);
                }
            }
        }
        dc.Pop(); DrawHud(dc, scale, origin);
    }

    private (double Scale, Point Origin) Transform()
    {
        if (document is null) return (1, new());
        var fit = Math.Min(ActualWidth / document.ImageWidth, ActualHeight / document.ImageHeight) * .97;
        var scale = fit * zoom;
        return (scale, new Point((ActualWidth - document.ImageWidth * scale) / 2 + pan.X, (ActualHeight - document.ImageHeight * scale) / 2 + pan.Y));
    }

    private void Wheel(object sender, MouseWheelEventArgs e) { SetZoom(zoom * (e.Delta > 0 ? 1.16 : 1 / 1.16), e.GetPosition(this)); e.Handled = true; }
    private void SetZoom(double next, Point pivot)
    {
        if (document is null) return; next = Math.Clamp(next, .2, 8); var (oldScale, oldOrigin) = Transform(); var imagePoint = new Point((pivot.X - oldOrigin.X) / oldScale, (pivot.Y - oldOrigin.Y) / oldScale);
        zoom = next; var fit = Math.Min(ActualWidth / document.ImageWidth, ActualHeight / document.ImageHeight) * .97; var newScale = fit * zoom;
        var baseOrigin = new Point((ActualWidth - document.ImageWidth * newScale) / 2, (ActualHeight - document.ImageHeight * newScale) / 2);
        pan = new Vector(pivot.X - imagePoint.X * newScale - baseOrigin.X, pivot.Y - imagePoint.Y * newScale - baseOrigin.Y);
        InvalidateVisual(); ZoomChanged?.Invoke(ZoomPercent);
    }
    private void Down(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount == 2)
        {
            Fit();
            e.Handled = true;
            return;
        }

        Focus();
        dragging = true;
        dragMoved = false;
        dragStart = e.GetPosition(this);
        panStart = pan;
        CaptureMouse();
        Cursor = Cursors.Hand;
        e.Handled = true;
    }

    private void Up(object sender, MouseButtonEventArgs e)
    {
        var point = e.GetPosition(this);
        dragging = false;
        ReleaseMouseCapture();
        Cursor = Cursors.Arrow;

        if (!dragMoved)
        {
            selectedMarker = HitTestMarker(point);
            MarkerSelected?.Invoke(selectedMarker);
            InvalidateVisual();
        }

        e.Handled = true;
    }

    private void Move(object sender, MouseEventArgs e)
    {
        mousePoint = e.GetPosition(this);
        if (dragging)
        {
            var current = mousePoint;
            if ((current - dragStart).Length > 3)
                dragMoved = true;
            if (dragMoved)
            {
                pan = panStart + (current - dragStart);
                InvalidateVisual();
            }
            return;
        }
        UpdateTooltip(mousePoint);
    }

    private MapMarker? HitTestMarker(Point point)
    {
        if (document is null || !ShowIcons)
            return null;

        var (scale, origin) = Transform();
        MapMarker? nearest = null;
        var best = double.MaxValue;
        foreach (var marker in document.Markers)
        {
            if (!ShowEnvironment && IsEnvironment(marker.Category))
                continue;

            var p = document.Project(marker.X, marker.Z);
            var sx = origin.X + p.X * scale;
            var sy = origin.Y + p.Y * scale;
            var distance = Math.Sqrt(Math.Pow(point.X - sx, 2) + Math.Pow(point.Y - sy, 2));
            var hitRadius = Math.Max(9, DisplayIconSide(marker.Category, scale) * .72);
            if (distance <= hitRadius && distance < best)
            {
                best = distance;
                nearest = marker;
            }
        }

        return nearest;
    }

    private void UpdateTooltip(Point point)
    {
        var nearest = HitTestMarker(point);
        if (nearest is null)
        {
            ToolTip = null;
            return;
        }

        ToolTip = $"{nearest.Name}\nX {nearest.X:0.0}  Z {nearest.Z:0.0}";
    }

    public void Export(string path)
    {
        if (document?.Image is null) throw new InvalidOperationException("Dışa aktarılacak harita yok.");
        var visual = new DrawingVisual(); using (var dc = visual.RenderOpen()) { dc.DrawImage(document.Image, new Rect(0, 0, document.ImageWidth, document.ImageHeight)); if (ShowIcons) foreach (var marker in document.Markers) { if (!ShowEnvironment && IsEnvironment(marker.Category)) continue; var p = document.Project(marker.X, marker.Z); var side = ExportIconSide(marker.Category); var icon = IconCache.Get(marker.IconFile); dc.DrawImage(icon, IconRect(icon, p, side)); } }
        var target = new RenderTargetBitmap(document.ImageWidth, document.ImageHeight, 96, 96, PixelFormats.Pbgra32); target.Render(visual); var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(target)); using var stream = File.Create(path); encoder.Save(stream);
    }

    private void DrawGrid(DrawingContext dc) { var pen = new Pen(new SolidColorBrush(Color.FromArgb(80, 24, 65, 76)), 1); for (var x = 0d; x < ActualWidth; x += 52) dc.DrawLine(pen, new Point(x, 0), new Point(x, ActualHeight)); for (var y = 0d; y < ActualHeight; y += 52) dc.DrawLine(pen, new Point(0, y), new Point(ActualWidth, y)); }
    private void DrawEmpty(DrawingContext dc)
    {
        var frame = new Rect(45, 45, Math.Max(0, ActualWidth - 90), Math.Max(0, ActualHeight - 90));
        dc.DrawRoundedRectangle(new SolidColorBrush(Color.FromArgb(70, 12, 28, 36)), new Pen(new SolidColorBrush(Color.FromArgb(110, 41, 74, 87)), 1), frame, 14, 14);
        var center = new Point(ActualWidth / 2, ActualHeight / 2 - 65);
        var glow = new SolidColorBrush(Color.FromArgb(30, 49, 201, 164));
        var pen = new Pen(new SolidColorBrush(Color.FromRgb(49, 201, 164)), 2.5);
        dc.DrawEllipse(glow, pen, center, 50, 50);
        dc.DrawEllipse(null, new Pen(new SolidColorBrush(Color.FromArgb(90, 49, 201, 164)), 1), center, 68, 68);
        dc.DrawLine(pen, new Point(center.X - 24, center.Y), new Point(center.X + 24, center.Y));
        dc.DrawLine(pen, new Point(center.X, center.Y - 24), new Point(center.X, center.Y + 24));
        var dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        var title = new FormattedText("Canlı harita önizlemesi", System.Globalization.CultureInfo.CurrentCulture, FlowDirection.LeftToRight, new Typeface("Segoe UI Semibold"), 21, Brushes.White, dpi);
        var subtitle = new FormattedText("Sağdaki ayarları tamamlayın; üretilen harita burada açılacak", System.Globalization.CultureInfo.CurrentCulture, FlowDirection.LeftToRight, new Typeface("Segoe UI"), 12, new SolidColorBrush(Color.FromRgb(132, 157, 174)), dpi);
        dc.DrawText(title, new Point(center.X - title.Width / 2, center.Y + 78));
        dc.DrawText(subtitle, new Point(center.X - subtitle.Width / 2, center.Y + 110));
        var steps = new[] { "1   Dünya ayarlarını seç", "2   Monument kurallarını belirle", "3   Haritayı üret" };
        const double stepWidth = 185;
        const double gap = 10;
        var totalWidth = steps.Length * stepWidth + (steps.Length - 1) * gap;
        var startX = center.X - totalWidth / 2;
        for (var i = 0; i < steps.Length; i++)
        {
            var box = new Rect(startX + i * (stepWidth + gap), center.Y + 155, stepWidth, 42);
            dc.DrawRoundedRectangle(new SolidColorBrush(Color.FromArgb(125, 15, 31, 40)), new Pen(new SolidColorBrush(Color.FromArgb(120, 44, 72, 83)), 1), box, 7, 7);
            var step = new FormattedText(steps[i], System.Globalization.CultureInfo.CurrentCulture, FlowDirection.LeftToRight, new Typeface("Segoe UI Semibold"), 11, new SolidColorBrush(Color.FromRgb(185, 204, 216)), dpi);
            dc.DrawText(step, new Point(box.X + (box.Width - step.Width) / 2, box.Y + 13));
        }
    }
    private void DrawHud(DrawingContext dc, double scale, Point origin) { var text = new FormattedText($"{ZoomPercent:0}%   X {mousePoint.X:0}  Y {mousePoint.Y:0}", System.Globalization.CultureInfo.CurrentCulture, FlowDirection.LeftToRight, new Typeface("Cascadia Mono"), 11, new SolidColorBrush(Color.FromRgb(190, 205, 216)), VisualTreeHelper.GetDpi(this).PixelsPerDip); var rect = new Rect(ActualWidth - text.Width - 26, ActualHeight - 35, text.Width + 16, 25); dc.DrawRoundedRectangle(new SolidColorBrush(Color.FromArgb(210, 8, 13, 18)), null, rect, 5, 5); dc.DrawText(text, new Point(rect.X + 8, rect.Y + 5)); }
    private double DisplayIconSide(string category, double scale)
    {
        var compact = IsCompactIcon(category);
        var sourcePixels = IconSize * 1.5 * (compact ? .52 : 1);
        return Math.Clamp(sourcePixels * scale, compact ? 4 : 6, compact ? 24 : 54);
    }
    private double ExportIconSide(string category) => IconSize * 1.5 * (IsCompactIcon(category) ? .52 : 1);
    private static Rect IconRect(ImageSource icon, Point center, double maxSide)
    {
        var width = Math.Max(1, icon.Width);
        var height = Math.Max(1, icon.Height);
        var scale = maxSide / Math.Max(width, height);
        var drawWidth = width * scale;
        var drawHeight = height * scale;
        return new Rect(center.X - drawWidth / 2, center.Y - drawHeight / 2, drawWidth, drawHeight);
    }
    private static bool IsCompactIcon(string id) => id is "powerlines" or "power_substations" or "metro_entrances"
        or "caves" or "icebergs" or "god_rocks" or "lakes" or "canyons"
        or "oases" or "swamps" or "water_wells" or "ruins";
    private static bool IsEnvironment(string id) => id is "caves" or "canyons" or "lakes" or "oases" or "icebergs" or "powerlines" or "power_substations" or "metro_entrances" or "god_rocks";
}
