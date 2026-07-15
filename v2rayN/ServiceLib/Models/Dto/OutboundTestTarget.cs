namespace ServiceLib.Models.Dto;

public record OutboundTestTarget(string Tag, int Order, IReadOnlyList<string> ChainTags);
