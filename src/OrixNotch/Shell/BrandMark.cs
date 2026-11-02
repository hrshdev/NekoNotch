using System.Globalization;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Xml.Linq;
using OrixNotch.Services;

namespace OrixNotch.Shell;

/// <summary>Which artwork to render. <see cref="Auto"/> follows the theme: gradient on dark, near-black mono on light.</summary>
public enum BrandVariant
{
    Auto,
    Gradient,
    MonoWhite,
    MonoDark,
    Outline,
}

/// <summary>Brand colours: warm glowing eyes (#FFF382 → #FFE066).</summary>
public static class BrandPalette
{
    public static readonly Color EyeYellow = Color.FromRgb(0xFF, 0xF3, 0x82);
    public static readonly Color EyeGlow = Color.FromRgb(0xFF, 0xE0, 0x66);
    public static readonly Color MonoDark = Color.FromRgb(0x11, 0x13, 0x18);

    public static LinearGradientBrush CreateGradientBrush()
    {
        var brush = new LinearGradientBrush(EyeYellow, EyeGlow, new Point(0, 0), new Point(0, 1));
        brush.Freeze();
        return brush;
    }
}

/// <summary>
/// The NekoNotch mark (the two glowing eyes), drawn from Assets/Brand/mark.svg. Keeps its aspect ratio, centred in the control.
/// </summary>
public class BrandMark : Control
{
    private static readonly Geometry Mark = LoadMark();
    private static readonly LinearGradientBrush Gradient = BrandPalette.CreateGradientBrush();
    private static readonly SolidColorBrush WhiteBrush = Freeze(new SolidColorBrush(Colors.White));
    private static readonly SolidColorBrush DarkBrush = Freeze(new SolidColorBrush(BrandPalette.MonoDark));

    public static readonly DependencyProperty VariantProperty = DependencyProperty.Register(
        nameof(Variant), typeof(BrandVariant), typeof(BrandMark),
        new FrameworkPropertyMetadata(BrandVariant.Auto, FrameworkPropertyMetadataOptions.AffectsRender));

    static BrandMark()
    {
        FocusableProperty.OverrideMetadata(typeof(BrandMark), new FrameworkPropertyMetadata(false));
    }

    public BrandVariant Variant
    {
        get => (BrandVariant)GetValue(VariantProperty);
        set => SetValue(VariantProperty, value);
    }

    private BrandVariant EffectiveVariant()
    {
        if (Variant != BrandVariant.Auto) return Variant;
        try
        {
            return ThemeService.CurrentScheme.IsLight ? BrandVariant.MonoDark : BrandVariant.Gradient;
        }
        catch
        {
            return BrandVariant.Gradient;
        }
    }

    protected override void OnRender(DrawingContext dc)
    {
        base.OnRender(dc);
        var variant = EffectiveVariant();
        Draw(dc, RenderSize, variant switch
        {
            BrandVariant.MonoWhite => WhiteBrush,
            BrandVariant.MonoDark => DarkBrush,
            BrandVariant.Outline => null,
            _ => Gradient,
        }, variant == BrandVariant.Outline ? Foreground : null);
    }

    /// <summary>The mark, kept in proportion and centred in <paramref name="size"/>.</summary>
    private static void Draw(DrawingContext dc, Size size, Brush? fill, Brush? outline = null)
    {
        var bounds = Mark.Bounds;
        if (size.Width <= 0 || size.Height <= 0 || bounds.IsEmpty) return;
        var scale = Math.Min(size.Width / bounds.Width, size.Height / bounds.Height) * 0.88;
        dc.PushTransform(new TranslateTransform((size.Width - bounds.Width * scale) / 2, (size.Height - bounds.Height * scale) / 2));
        dc.PushTransform(new ScaleTransform(scale, scale));
        dc.DrawGeometry(fill ?? Brushes.Transparent, outline is null ? null : new Pen(outline, 1.5 / scale) { LineJoin = PenLineJoin.Round }, Mark);
        dc.Pop();
        dc.Pop();
    }

    private static Geometry LoadMark()
    {
        var group = new GeometryGroup { FillRule = FillRule.EvenOdd };
        try
        {
            var resource = Application.GetResourceStream(new Uri("pack://application:,,,/Assets/Brand/mark.svg"));
            if (resource != null)
            {
                using var stream = resource.Stream;
                foreach (var path in XDocument.Load(stream).Descendants().Where(e => e.Name.LocalName == "path"))
                {
                    var geometry = Geometry.Parse(path.Attribute("d")?.Value ?? "").Clone();
                    var move = Regex.Match(path.Attribute("transform")?.Value ?? "", @"translate\(\s*([-\d.]+)[\s,]+([-\d.]+)\s*\)");
                    if (move.Success)
                        geometry.Transform = new TranslateTransform(
                            double.Parse(move.Groups[1].Value, CultureInfo.InvariantCulture),
                            double.Parse(move.Groups[2].Value, CultureInfo.InvariantCulture));
                    group.Children.Add(geometry);
                }
            }
        }
        catch (Exception ex)
        {
            App.Log(ex);
        }

        if (group.Children.Count == 0)
        {
            group.Children.Add(Geometry.Parse("M 36,32 A 8,8 0 0,1 44,40 L 44,60 A 8,8 0 0,1 36,68 A 8,8 0 0,1 28,60 L 28,40 A 8,8 0 0,1 36,32 Z"));
            group.Children.Add(Geometry.Parse("M 64,32 A 8,8 0 0,1 72,40 L 72,60 A 8,8 0 0,1 64,68 A 8,8 0 0,1 56,60 L 56,40 A 8,8 0 0,1 64,32 Z"));
        }

        var bounds = group.Bounds;
        if (!bounds.IsEmpty)
        {
            group.Transform = new TranslateTransform(-bounds.X, -bounds.Y);
        }
        group.Freeze();
        return group;
    }

    private static SolidColorBrush Freeze(SolidColorBrush b)
    {
        b.Freeze();
        return b;
    }
}
