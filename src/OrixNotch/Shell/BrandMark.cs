using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace OrixNotch.Shell;

/// <summary>Which artwork to render. Maintained for API compatibility.</summary>
public enum BrandVariant
{
    Auto,
    Gradient,
    MonoWhite,
    MonoDark,
    Outline,
}

/// <summary>Brand colours: Coucou Mochi pet palette.</summary>
public static class BrandPalette
{
    public static readonly Color EyeWhite = Colors.White;
    public static readonly Color EyeDark = Color.FromRgb(0x1A, 0x14, 0x12);
    public static readonly Color BlushPink = Color.FromArgb(115, 0xFF, 0x78, 0x96);
    public static readonly Color MonoDark = Color.FromRgb(0x11, 0x13, 0x18);

    public static SolidColorBrush CreateBrush() => Brushes.White;
}

/// <summary>
/// The NekoNotch mark (the Mochi pet), rendered crisply with WPF geometry.
/// </summary>
public class BrandMark : Control
{
    private static readonly LinearGradientBrush BodyBrush = CreateBodyBrush();
    private static readonly RadialGradientBrush GlossBrush = CreateGlossBrush();
    private static readonly SolidColorBrush StrokeBrush = Freeze(new SolidColorBrush(Color.FromArgb(140, 0xB0, 0xB2, 0xB8)));
    private static readonly SolidColorBrush BlushBrush = Freeze(new SolidColorBrush(Color.FromArgb(115, 0xFF, 0x78, 0x96)));
    private static readonly SolidColorBrush EyeBrush = Freeze(new SolidColorBrush(Color.FromRgb(0x1A, 0x14, 0x12)));
    private static readonly SolidColorBrush GlintBrush = Freeze(new SolidColorBrush(Color.FromArgb(242, 0xFF, 0xFF, 0xFF)));

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

    protected override void OnRender(DrawingContext dc)
    {
        base.OnRender(dc);
        DrawPet(dc, RenderSize);
    }

    /// <summary>Draws the Coucou Mochi squircle pet centered within <paramref name="size"/>.</summary>
    public static void DrawPet(DrawingContext dc, Size size)
    {
        if (size.Width <= 0 || size.Height <= 0) return;

        var scale = Math.Min(size.Width, size.Height);
        var targetWidth = scale * 0.92;
        var targetHeight = targetWidth * (24.0 / 38.0);
        var cx = size.Width / 2.0;
        var cy = size.Height / 2.0;

        // Mochi Squircle Body (CornerRadius 8 on 38x24 => 8/24)
        var radius = targetHeight * (8.0 / 24.0);
        var x = cx - targetWidth / 2.0;
        var y = cy - targetHeight / 2.0;
        var rect = new Rect(x, y, targetWidth, targetHeight);

        var strokePen = new Pen(StrokeBrush, Math.Max(0.6, targetHeight * 0.03));
        strokePen.Freeze();
        dc.DrawRoundedRectangle(BodyBrush, strokePen, rect, radius, radius);

        // Top specular gloss highlight
        dc.DrawEllipse(GlossBrush, null, new Point(cx, y + targetHeight * 0.18), targetWidth * 0.35, targetHeight * 0.22);

        // Rosy Blush Cheeks (compact width)
        var blushRx = targetWidth * (1.6 / 38.0);
        var blushRy = targetHeight * (1.2 / 24.0);
        var blushY = y + targetHeight * 0.58;
        dc.DrawEllipse(BlushBrush, null, new Point(x + targetWidth * 0.14, blushY), blushRx, blushRy);
        dc.DrawEllipse(BlushBrush, null, new Point(x + targetWidth * 0.86, blushY), blushRx, blushRy);

        // Espresso ink eye pills
        var eyeW = targetWidth * (4.8 / 38.0);
        var eyeH = targetHeight * (8.0 / 24.0);
        var eyeRadius = eyeW / 2.0;
        var eyeY = y + (targetHeight - eyeH) / 2.0 + (targetHeight * 0.01);

        var leftEyeX = cx - targetWidth * 0.18 - eyeW / 2.0;
        var rightEyeX = cx + targetWidth * 0.18 - eyeW / 2.0;
        dc.DrawRoundedRectangle(EyeBrush, null, new Rect(leftEyeX, eyeY, eyeW, eyeH), eyeRadius, eyeRadius);
        dc.DrawRoundedRectangle(EyeBrush, null, new Rect(rightEyeX, eyeY, eyeW, eyeH), eyeRadius, eyeRadius);

        // Specular glint dots
        var glintR = Math.Max(0.6, eyeW * 0.20);
        dc.DrawEllipse(GlintBrush, null, new Point(leftEyeX + eyeW * 0.38, eyeY + eyeH * 0.26), glintR, glintR);
        dc.DrawEllipse(GlintBrush, null, new Point(rightEyeX + eyeW * 0.38, eyeY + eyeH * 0.26), glintR, glintR);
    }

    private static LinearGradientBrush CreateBodyBrush()
    {
        var b = new LinearGradientBrush
        {
            StartPoint = new Point(0.5, 0),
            EndPoint = new Point(0.5, 1)
        };
        b.GradientStops.Add(new GradientStop(Color.FromRgb(0xF4, 0xF4, 0xF6), 0.0));
        b.GradientStops.Add(new GradientStop(Color.FromRgb(0xED, 0xED, 0xEF), 0.4));
        b.GradientStops.Add(new GradientStop(Color.FromRgb(0xC5, 0xC6, 0xCB), 1.0));
        b.Freeze();
        return b;
    }

    private static RadialGradientBrush CreateGlossBrush()
    {
        var b = new RadialGradientBrush
        {
            Center = new Point(0.5, 0.25),
            GradientOrigin = new Point(0.5, 0.25),
            RadiusX = 0.5,
            RadiusY = 0.5
        };
        b.GradientStops.Add(new GradientStop(Color.FromArgb(150, 255, 255, 255), 0.0));
        b.GradientStops.Add(new GradientStop(Color.FromArgb(0, 255, 255, 255), 1.0));
        b.Freeze();
        return b;
    }

    private static SolidColorBrush Freeze(SolidColorBrush b)
    {
        b.Freeze();
        return b;
    }
}
