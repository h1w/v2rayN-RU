using System.Reflection;

namespace v2rayN.Desktop.Common;

public static class ToastClassifier
{
    private static readonly Lazy<Dictionary<string, ToastType>> _map = new(BuildMap);

    public static ToastType Classify(string? message)
    {
        if (string.IsNullOrWhiteSpace(message))
        {
            return ToastType.Info;
        }

        return _map.Value.TryGetValue(message.Trim(), out var type) ? type : ToastType.Info;
    }

    private static ToastType CategoryFromKeyName(string keyName)
    {
        if (keyName.Contains("Success", StringComparison.OrdinalIgnoreCase))
        {
            return ToastType.Success;
        }
        if (keyName.Contains("Fail", StringComparison.OrdinalIgnoreCase)
            || keyName.Contains("Error", StringComparison.OrdinalIgnoreCase))
        {
            return ToastType.Error;
        }
        if (keyName.Contains("Please", StringComparison.OrdinalIgnoreCase)
            || keyName.Contains("Invalid", StringComparison.OrdinalIgnoreCase)
            || keyName.Contains("Warn", StringComparison.OrdinalIgnoreCase)
            || keyName.Contains("Tip", StringComparison.OrdinalIgnoreCase))
        {
            return ToastType.Warning;
        }
        return ToastType.Info;
    }

    private static Dictionary<string, ToastType> BuildMap()
    {
        var map = new Dictionary<string, ToastType>(StringComparer.Ordinal);
        try
        {
            var props = typeof(ResUI).GetProperties(BindingFlags.Public | BindingFlags.Static);
            foreach (var p in props)
            {
                if (p.PropertyType != typeof(string))
                {
                    continue;
                }

                var category = CategoryFromKeyName(p.Name);
                if (category == ToastType.Info)
                {
                    continue;
                }

                if (p.GetValue(null) is string value && !string.IsNullOrWhiteSpace(value))
                {
                    map[value.Trim()] = category;
                }
            }
        }
        catch
        {
            // Сбой рефлексии → пустой словарь → всё классифицируется как Info.
        }
        return map;
    }
}
