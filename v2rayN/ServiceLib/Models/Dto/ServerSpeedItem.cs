namespace ServiceLib.Models.Dto;

[Serializable]
public class ServerSpeedItem : ServerStatItem
{
    // Bytes per second, or null when this profile has no available live sample.
    public double? ProxyUpRate { get; set; }
    public double? ProxyDownRate { get; set; }
    public double? DirectUpRate { get; set; }
    public double? DirectDownRate { get; set; }
}
