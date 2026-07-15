using System.Text.Json;
using System.Text.Json.Serialization;

namespace ServiceLib.Handler;

/// <summary>
/// Pure (de)serialization for exporting/importing routing rules and rule-sets.
/// Keeps VM code DRY and lets the logic be unit-tested without app singletons.
/// </summary>
public static class RoutingRuleExporter
{
    private static readonly JsonSerializerOptions _exportOptions = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    /// <summary>Serialize rules as an ordered List&lt;RulesItem&gt; JSON with Id stripped.</summary>
    public static string ExportRulesToJson(IEnumerable<RulesItem> rules)
    {
        var lst = new List<RulesItem>();
        foreach (var it in rules)
        {
            var copy = JsonUtils.DeepCopy(it);
            if (copy == null)
            {
                continue;
            }
            copy.Id = null;
            lst.Add(copy);
        }
        return JsonUtils.Serialize(lst, _exportOptions);
    }

    /// <summary>Parse a List&lt;RulesItem&gt; JSON, assigning a fresh Id to each rule. Null on failure.</summary>
    public static List<RulesItem>? ParseRulesJson(string? json)
    {
        if (json.IsNullOrEmpty())
        {
            return null;
        }
        var lst = JsonUtils.Deserialize<List<RulesItem>>(json);
        if (lst == null)
        {
            return null;
        }
        foreach (var rule in lst)
        {
            rule.Id = Utils.GetGuid(false);
        }
        return lst;
    }

    /// <summary>Serialize rule-sets as a RoutingTemplate, stripping instance-specific fields.</summary>
    public static string ExportRoutingTemplateToJson(IEnumerable<RoutingItem> items)
    {
        var lst = new List<RoutingItem>();
        foreach (var it in items)
        {
            var copy = JsonUtils.DeepCopy(it);
            if (copy == null)
            {
                continue;
            }
            copy.Id = null;
            copy.IsActive = false;
            copy.Sort = 0;
            copy.Locked = false;
            lst.Add(copy);
        }
        var template = new RoutingTemplate
        {
            Version = Utils.GetVersion(false),
            RoutingItems = lst.ToArray(),
        };
        return JsonUtils.Serialize(template, _exportOptions);
    }

    /// <summary>Parse a RoutingTemplate JSON, clearing each item's Id. Null on failure/empty.</summary>
    public static List<RoutingItem>? ParseRoutingTemplateJson(string? json)
    {
        if (json.IsNullOrEmpty())
        {
            return null;
        }
        var template = JsonUtils.Deserialize<RoutingTemplate>(json);
        if (template?.RoutingItems == null || template.RoutingItems.Length == 0)
        {
            return null;
        }
        var lst = template.RoutingItems.ToList();
        foreach (var it in lst)
        {
            it.Id = null;
        }
        return lst;
    }
}
