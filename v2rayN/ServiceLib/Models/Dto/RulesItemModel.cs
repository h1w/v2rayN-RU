namespace ServiceLib.Models.Dto;

[Serializable]
public class RulesItemModel : RulesItem
{
    public string InboundTags { get; set; }
    public string Ips { get; set; }
    public string Domains { get; set; }
    public string Protocols { get; set; }
    public string RuleTypeName { get; set; }

    public bool IsReadonly { get; set; }
    public bool IsEditable => !IsReadonly;

    /// <summary>
    /// Origin marker shown in a dedicated column: mandatory rules parsed from the
    /// active custom JSON config are tagged, user rules are left blank.
    /// </summary>
    public string RuleSource { get; set; }

    public int RawOrdinal { get; set; }
    public bool CanEditCustom { get; set; }

    /// <summary>
    /// Можно ли переключать инлайн-чекбокс этой строки. Локальные правила —
    /// как прежде (по IsEditable); JSON-правила — только при разрешённом
    /// управлении (CanEditCustom). Так поведение обычных правил не меняется.
    /// </summary>
    public bool CanToggleInline => IsReadonly ? CanEditCustom : IsEditable;
}
