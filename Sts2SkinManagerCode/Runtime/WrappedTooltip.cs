using Godot;

namespace Sts2SkinManager.Runtime;

// Controls whose tooltip wraps instead of running off the screen as one long line.
//
// Godot's stock tooltip is a PopupPanel containing a Label with autowrap OFF, so a sentence-long
// TooltipText renders as a single line — `blocked_mod_toggle_tooltip` is 242 characters. Autowrap is
// a property, not a theme item, so it cannot be fixed from a Theme; the only hook is
// _MakeCustomTooltip, and that has to be overridden on the concrete Control subclass. Hence one thin
// adapter per control type we attach a long tooltip to, all deferring to Build.
//
// Godot still wraps whatever we return in the themed TooltipPanel, so tooltips keep the game's look;
// we only swap the inner Label for one with autowrap on.
public static class WrappedTooltip
{
    public const float DefaultWidth = 360f;

    // The width has to come from CustomMinimumSize: an autowrapping Label reports its full
    // single-line width as its minimum unless something constrains it, and the tooltip popup sizes
    // itself to that minimum — so without a width it would still render as one line.
    public static GodotObject Build(string forText, float width) => new Label
    {
        Text = forText,
        AutowrapMode = TextServer.AutowrapMode.WordSmart,
        CustomMinimumSize = new Vector2(width, 0),
    };
}

public partial class WrappedTooltipLabel : Label
{
    public float TooltipWidth { get; init; } = WrappedTooltip.DefaultWidth;

    public override GodotObject _MakeCustomTooltip(string forText) => WrappedTooltip.Build(forText, TooltipWidth);
}

public partial class WrappedTooltipButton : Button
{
    public float TooltipWidth { get; init; } = WrappedTooltip.DefaultWidth;

    public override GodotObject _MakeCustomTooltip(string forText) => WrappedTooltip.Build(forText, TooltipWidth);
}

public partial class WrappedTooltipCheckBox : CheckBox
{
    public float TooltipWidth { get; init; } = WrappedTooltip.DefaultWidth;

    public override GodotObject _MakeCustomTooltip(string forText) => WrappedTooltip.Build(forText, TooltipWidth);
}
