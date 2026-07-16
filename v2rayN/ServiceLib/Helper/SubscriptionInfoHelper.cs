using System.Globalization;

namespace ServiceLib.Helper;

public record SubscriptionUserinfo(long? Upload, long? Download, long? Total, long? Expire);

/// <summary>
/// Parses the subscription server's Subscription-Userinfo and Profile-Title response headers.
/// </summary>
public static class SubscriptionInfoHelper
{
    public static SubscriptionUserinfo? ParseUserinfo(string? header)
    {
        if (header.IsNullOrEmpty())
        {
            return null;
        }

        long? upload = null, download = null, total = null, expire = null;
        var parts = header!.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        foreach (var part in parts)
        {
            var kv = part.Split('=', 2);
            if (kv.Length != 2)
            {
                continue;
            }

            var key = kv[0].Trim().ToLowerInvariant();
            if (!long.TryParse(kv[1].Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var value))
            {
                continue;
            }

            switch (key)
            {
                case "upload": upload = value; break;
                case "download": download = value; break;
                case "total": total = value; break;
                case "expire": expire = value; break;
            }
        }

        if (upload is null && download is null && total is null && expire is null)
        {
            return null;
        }

        return new SubscriptionUserinfo(upload, download, total, expire);
    }

    public static string? DecodeProfileTitle(string? header)
    {
        if (header.IsNullOrEmpty())
        {
            return null;
        }

        var value = header!.Trim();
        if (value.StartsWith("base64:", StringComparison.OrdinalIgnoreCase))
        {
            var decoded = Utils.Base64Decode(value["base64:".Length..]);
            return decoded.IsNullOrEmpty() ? null : decoded.Trim();
        }

        return value;
    }

    public static (string Remarks, bool AutoRemark) ResolveRemarkOnSave(string? entered, string originalRemarks, bool wasAuto, string urlHost)
    {
        if (entered.IsNullOrEmpty())
        {
            return (urlHost, true);
        }

        // User changed the text => manual; unchanged => keep prior auto/manual state.
        if (entered != originalRemarks)
        {
            return (entered!, false);
        }

        return (entered!, wasAuto);
    }

    public static string ResolveRemarkOnUpdate(string currentRemarks, bool autoRemark, string? profileTitle, string urlHost)
    {
        if (!autoRemark)
        {
            return currentRemarks;
        }

        var title = DecodeProfileTitle(profileTitle);
        if (!title.IsNullOrEmpty())
        {
            return title!;
        }

        return currentRemarks.IsNullOrEmpty() ? urlHost : currentRemarks;
    }
}
