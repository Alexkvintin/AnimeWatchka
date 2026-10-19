using Avalonia.Media;
using Avalonia.Media.TextFormatting;

namespace AnimeApp.UI;
public sealed class HeartTrimming : TextTrimming
{
    public static HeartTrimming Instance { get; } = new();
    public override TextCollapsingProperties CreateCollapsingProperties(TextCollapsingCreateInfo createInfo) =>
        new TextTrailingCharacterEllipsis("♥♥♥", createInfo.Width, createInfo.TextRunProperties, createInfo.FlowDirection);
}
