using Lumina.Text.ReadOnly;

namespace OmenTools.OmenService;

public class TooltipModification
{
    public required TooltipModificationType Type { get; init; }
    public required ReadOnlySeString        Text { get; init; }
}
