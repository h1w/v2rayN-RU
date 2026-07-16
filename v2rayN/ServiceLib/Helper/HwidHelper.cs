namespace ServiceLib.Helper;

/// <summary>
/// Happ-compatible HWID generation, validation and subscription header assembly.
/// The stored value is always sent verbatim; formatting options affect generation only.
/// </summary>
public static class HwidHelper
{
    // Remnawave Panel v2.9.0+ rejects anything that does not match this.
    private static readonly Regex _hwidRegex = new(@"^[a-zA-Z0-9=-]{10,64}$", RegexOptions.Compiled);

    // Note: use !IsNullOrEmpty rather than IsNotEmpty. Extension.IsNotEmpty is annotated
    // [NotNullWhen(false)] (inverted), so it does not narrow the nullable for the regex call.
    public static bool IsValidHwid(string? hwid)
    {
        return !hwid.IsNullOrEmpty() && _hwidRegex.IsMatch(hwid);
    }

    public static string GenerateHwid(bool withoutHyphens)
    {
        return Guid.NewGuid().ToString(withoutHyphens ? "N" : "D");
    }

    public static string GetDeviceOs()
    {
        if (Utils.IsWindows())
        {
            return "Windows";
        }
        if (Utils.IsLinux())
        {
            return "Linux";
        }
        if (Utils.IsMacOS())
        {
            return "macOS";
        }
        return "Unknown";
    }

    public static string GetOsVersion()
    {
        return Environment.OSVersion.Version.ToString();
    }

    public static string GetDeviceLocale()
    {
        return System.Globalization.CultureInfo.CurrentUICulture.TwoLetterISOLanguageName;
    }

    /// <summary>
    /// Builds the Happ-compatible header set. Returns an empty dictionary when HWID
    /// is disabled or the stored value is invalid, so callers can pass it unconditionally.
    /// x-device-model is intentionally omitted: it has no hardware-free meaning on desktop.
    /// </summary>
    public static Dictionary<string, string> BuildSubscriptionHeaders(HwidItem? hwidItem)
    {
        var headers = new Dictionary<string, string>();

        if (hwidItem is not { Enabled: true } || !IsValidHwid(hwidItem.Hwid))
        {
            return headers;
        }

        headers.Add("x-hwid", hwidItem.Hwid!);
        headers.Add("x-device-os", GetDeviceOs());
        headers.Add("x-ver-os", GetOsVersion());
        headers.Add("x-device-locale", GetDeviceLocale());

        return headers;
    }
}
