namespace v2rayN.Desktop.Common;

public static class AppBuilderExtension
{
    private static readonly string DefaultFontFamilyName =
        Path.Combine(Global.AvaAssets, "Fonts#Noto Sans SC");

    // Color-emoji font bundled with the app. Segoe UI Emoji on Windows does not draw
    // regional-indicator flag emoji (e.g. country flags), so we ship Noto Color Emoji
    // inside the build and put it ahead of the OS fonts to guarantee flags render.
    private static readonly string EmojiFontFamilyName =
        Path.Combine(Global.AvaAssets, "Fonts#Noto Color Emoji");

    public static AppBuilder WithFontByDefault(this AppBuilder appBuilder)
    {
        var fallbacks = new List<FontFallback>();

        var notoSansSc = new FontFamily(DefaultFontFamilyName);

        fallbacks.Add(new FontFallback
        {
            FontFamily = notoSansSc
        });

        // Bundled emoji font takes priority over OS emoji fonts on every platform.
        AddFontFallback(fallbacks, EmojiFontFamilyName);

        if (OperatingSystem.IsWindows())
        {
            AddFontFallback(fallbacks, "Segoe UI Emoji");
            AddFontFallback(fallbacks, "Segoe UI Symbol");
        }
        else if (OperatingSystem.IsMacOS())
        {
            AddFontFallback(fallbacks, "Apple Color Emoji");
            AddFontFallback(fallbacks, "Apple Symbols");
        }
        else if (OperatingSystem.IsLinux())
        {
            AddFontFallback(fallbacks, "Noto Color Emoji");
            AddFontFallback(fallbacks, "Noto Sans Symbols");
        }

        return appBuilder.With(new FontManagerOptions
        {
            DefaultFamilyName = DefaultFontFamilyName,
            FontFallbacks = fallbacks.ToArray()
        });
    }

    private static void AddFontFallback(List<FontFallback> fallbacks, string fontFamilyName)
    {
        fallbacks.Add(new FontFallback
        {
            FontFamily = new FontFamily(fontFamilyName)
        });
    }
}
