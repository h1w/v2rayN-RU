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
}
