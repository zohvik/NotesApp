namespace NotesApp.Client.Theming;

// Swappable color themes. Each theme is just a set of named colors that we push
// into Application.Resources at runtime. Because the UI references these colors
// with {DynamicResource ...}, overwriting them repaints the app live.
public static class ThemeManager
{
    // The color keys every theme must define. These match the DynamicResource keys used in MainPage.xaml.
    private static readonly string[] Keys =
        { "Bg", "BgSidebar", "BgList", "Divider", "TextPrimary", "TextMuted", "RowSelected", "Accent" };

    public record Theme(string Name, string Bg, string BgSidebar, string BgList,
        string Divider, string TextPrimary, string TextMuted, string RowSelected, string Accent);

    public static readonly IReadOnlyList<Theme> Themes = new List<Theme>
    {
        // Name,                Bg,        BgSidebar, BgList,    Divider,   TextPrimary, TextMuted, RowSelected, Accent
        new("Dark",             "#191919", "#202020", "#1C1C1C", "#2F2F2F", "#E9E9E7",   "#8B8B8B", "#2C2C2C",   "#5B8DEF"),
        new("Catppuccin Mocha", "#1E1E2E", "#11111B", "#181825", "#313244", "#CDD6F4",   "#A6ADC8", "#313244",   "#CBA6F7"),
        new("Catppuccin Macchiato","#24273A","#181926","#1E2030","#363A4F","#CAD3F5",    "#A5ADCB", "#363A4F",   "#C6A0F6"),
        new("Catppuccin Frappe","#303446", "#232634", "#292C3C", "#414559", "#C6D0F5",   "#A5ADCE", "#414559",   "#CA9EE6"),
        new("Catppuccin Latte", "#EFF1F5", "#DCE0E8", "#E6E9EF", "#CCD0DA", "#4C4F69",   "#6C6F85", "#CCD0DA",   "#8839EF"),
    };

    public static IReadOnlyList<string> ThemeNames => Themes.Select(t => t.Name).ToList();

    public static string Current => Preferences.Get("theme", "Dark");

    public static Theme CurrentTheme => Themes.FirstOrDefault(t => t.Name == Current) ?? Themes[0];

    // Writes the chosen theme's colors into Application.Resources and remembers it.
    public static void Apply(string name)
    {
        var theme = Themes.FirstOrDefault(t => t.Name == name) ?? Themes[0];
        var resources = Application.Current?.Resources;
        if (resources is null)
        {
            return;
        }

        resources["Bg"] = Color.FromArgb(theme.Bg);
        resources["BgSidebar"] = Color.FromArgb(theme.BgSidebar);
        resources["BgList"] = Color.FromArgb(theme.BgList);
        resources["Divider"] = Color.FromArgb(theme.Divider);
        resources["TextPrimary"] = Color.FromArgb(theme.TextPrimary);
        resources["TextMuted"] = Color.FromArgb(theme.TextMuted);
        resources["RowSelected"] = Color.FromArgb(theme.RowSelected);
        resources["Accent"] = Color.FromArgb(theme.Accent);

        Preferences.Set("theme", theme.Name);
    }

    // Called once at startup so the DynamicResource color keys exist before the UI loads.
    public static void ApplySaved() => Apply(Current);
}
