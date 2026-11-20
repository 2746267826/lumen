using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Effects;
using Microsoft.Win32;

namespace Lumen.Windows.Services;

public static class ThemeManager
{
    public const string SystemMode = "system";
    public const string LightMode = "light";
    public const string DarkMode = "dark";

    private static readonly Dictionary<string, (string Dark, string Light)> Palette = new()
    {
        ["BgBrush"] = ("#0A0C14", "#F2F5FA"),
        ["TitleBarBrush"] = ("#0B0E17", "#FFFFFF"),
        ["SidebarBrush"] = ("#0D101A", "#FFFFFF"),
        ["SurfaceBrush"] = ("#111522", "#FFFFFF"),
        ["SurfaceAltBrush"] = ("#161B2B", "#F4F6FB"),
        ["ElevatedBrush"] = ("#1A2031", "#E9EDF6"),
        ["SoftCardBrush"] = ("#101420", "#F7F9FD"),
        ["ModalBrush"] = ("#121725", "#FFFFFF"),
        ["ShareHeroBrush"] = ("#13182A", "#F7F9FE"),
        ["DetailBrush"] = ("#0F1320", "#FFFFFF"),
        ["FooterBrush"] = ("#0E121D", "#F7F9FD"),
        ["InputBrush"] = ("#0E121C", "#FBFCFE"),
        ["HeroPanelBrush"] = ("#10141F", "#FBFCFE"),
        ["IconTileBrush"] = ("#161C2E", "#EFF3FA"),
        ["CardHoverBrush"] = ("#141929", "#F7F8FC"),
        ["CardPressedBrush"] = ("#181E31", "#EEF1F8"),
        ["NavHoverBrush"] = ("#141927", "#F0F2F9"),
        ["NavActiveBrush"] = ("#1A1D32", "#EAE7FF"),
        ["ChipBrush"] = ("#161C2C", "#F1F4FA"),
        ["ChipBorderBrush"] = ("#262E43", "#DEE4EF"),
        ["StatusPillBrush"] = ("#171D2C", "#F2F5FA"),
        ["StatusPillBorderBrush"] = ("#2A3247", "#E0E5F0"),
        ["BadgeBrush"] = ("#27224A", "#EDE9FF"),
        ["SecondaryButtonBrush"] = ("#1C2234", "#EDF0F7"),
        ["IconButtonHoverBrush"] = ("#20273A", "#E8ECF5"),
        ["FilterActiveBrush"] = ("#252047", "#E9E5FF"),
        ["LanIconBrush"] = ("#172521", "#E3F7F1"),
        ["RelayIconBrush"] = ("#201E32", "#EDE9FF"),
        ["SuccessTileBrush"] = ("#182B28", "#E3F7F1"),
        ["BorderBrush"] = ("#2A3248", "#D7DFEC"),
        ["BorderSoftBrush"] = ("#1C2233", "#E6EAF3"),
        ["BorderStrongBrush"] = ("#3A4360", "#C3CDDF"),
        ["ProgressTrackBrush"] = ("#252C40", "#E3E8F2"),
        ["TimelineBrush"] = ("#252D42", "#E0E5F0"),
        ["TextBrush"] = ("#F4F6FF", "#151A28"),
        ["MutedBrush"] = ("#909AB3", "#4F5A6E"),
        ["DimBrush"] = ("#656F88", "#6B7588"),
        ["FaintBrush"] = ("#707A93", "#667184"),
        ["SoftTextBrush"] = ("#B9C2D7", "#3C4659"),
        ["AccentLabelBrush"] = ("#8F86FF", "#5A4ED2"),
        ["AccentTextBrush"] = ("#C8BFFF", "#5A4ED2"),
        ["TealTextBrush"] = ("#B8F2E8", "#08796A"),
        ["OnAccentBrush"] = ("#0B0D14", "#0B0D14"),
        ["AccentBrush"] = ("#8D80FF", "#6C5FE0"),
        ["AccentDeepBrush"] = ("#6C5FE0", "#5A4ED2"),
        ["AccentSoftBrush"] = ("#28254C", "#ECE8FF"),
        ["TealBrush"] = ("#55DFC9", "#0E8C7C"),
        ["GreenBrush"] = ("#55DCA8", "#0C855B"),
        ["WarningBrush"] = ("#F1BD69", "#966310"),
        ["DangerBrush"] = ("#FF8795", "#C22F45"),
        ["SelectionBrush"] = ("#6458BF", "#B6ACFF"),
        ["CloseHoverBrush"] = ("#D83B4E", "#D83B4E"),
        ["OverlayBrush"] = ("#B3050710", "#660F172A"),
        ["ScrimBrush"] = ("#7A02040A", "#4D0F172A"),
        ["ToastBrush"] = ("#E61B2132", "#F7FFFFFF"),
        ["ToastBorderBrush"] = ("#343E59", "#D8DFEC")
    };

    private static readonly string[] HeroGradientDark = ["#202140", "#161B2C", "#112625"];
    private static readonly string[] HeroGradientLight = ["#EEF1FF", "#F6F9FF", "#E7FAF5"];

    public static bool IsLight { get; private set; }
    public static string EffectiveMode { get; private set; } = DarkMode;

    public static string Resolve(string? mode)
    {
        if (string.Equals(mode, LightMode, StringComparison.OrdinalIgnoreCase))
        {
            return LightMode;
        }

        if (string.Equals(mode, DarkMode, StringComparison.OrdinalIgnoreCase))
        {
            return DarkMode;
        }

        return SystemPrefersLight() ? LightMode : DarkMode;
    }

    public static void Apply(string? mode)
    {
        var effective = Resolve(mode);
        var light = effective == LightMode;
        var app = Application.Current;
        if (app is null)
        {
            EffectiveMode = effective;
            IsLight = light;
            return;
        }

        IsLight = light;
        EffectiveMode = effective;

        foreach (var (key, palette) in Palette)
        {
            if (app.TryFindResource(key) is SolidColorBrush brush)
            {
                TrySetColor(brush, Parse(light ? palette.Light : palette.Dark));
            }
        }

        ApplyGradient(app, "HeroGradient", light ? HeroGradientLight : HeroGradientDark);

        if (app.TryFindResource("CardShadow") is DropShadowEffect cardShadow)
        {
            if (!cardShadow.IsFrozen)
            {
                cardShadow.Opacity = light ? 0.12 : 0.22;
                cardShadow.ShadowDepth = light ? 4 : 8;
            }
        }

        if (app.TryFindResource("AccentGlow") is DropShadowEffect accentGlow)
        {
            if (!accentGlow.IsFrozen)
            {
                accentGlow.Opacity = light ? 0.20 : 0.34;
            }
        }
    }

    private static void ApplyGradient(
        Application app,
        string key,
        IReadOnlyList<string> colors)
    {
        if (app.TryFindResource(key) is not LinearGradientBrush gradient ||
            gradient.GradientStops.Count != colors.Count)
        {
            return;
        }

        for (var index = 0; index < colors.Count; index++)
        {
            var stop = gradient.GradientStops[index];
            if (stop.IsFrozen)
            {
                return;
            }

            stop.Color = Parse(colors[index]);
        }
    }

    private static void TrySetColor(SolidColorBrush brush, Color color)
    {
        try
        {
            if (brush.IsFrozen)
            {
                return;
            }

            brush.Color = color;
        }
        catch (InvalidOperationException)
        {
            // A frozen brush cannot be updated; leave the current palette intact.
        }
    }

    private static Color Parse(string hex) =>
        (Color)ColorConverter.ConvertFromString(hex);

    private static bool SystemPrefersLight()
    {
        if (!OperatingSystem.IsWindows())
        {
            return false;
        }

        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(
                @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            return key?.GetValue("AppsUseLightTheme") is int value && value != 0;
        }
        catch
        {
            return false;
        }
    }
}
