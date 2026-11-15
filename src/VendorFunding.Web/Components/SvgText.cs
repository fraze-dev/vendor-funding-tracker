using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Rendering;

namespace VendorFunding.Web.Components;

/// <summary>
/// Renders an SVG &lt;text&gt; element. Razor reserves &lt;text&gt; inside code blocks (such as @foreach),
/// so chart labels written there would silently disappear without this.
/// </summary>
public sealed class SvgText : ComponentBase
{
    [Parameter] public RenderFragment? ChildContent { get; set; }

    [Parameter(CaptureUnmatchedValues = true)] public Dictionary<string, object>? Attributes { get; set; }

    protected override void BuildRenderTree(RenderTreeBuilder builder)
    {
        builder.OpenElement(0, "text");
        builder.AddMultipleAttributes(1, Attributes);
        builder.AddContent(2, ChildContent);
        builder.CloseElement();
    }
}
