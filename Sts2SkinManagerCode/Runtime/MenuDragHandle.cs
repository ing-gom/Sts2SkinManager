using System;
using Godot;

namespace Sts2SkinManager.Runtime;

// A compact grip that lets the user drag the whole skin-manager overlay to any corner/edge,
// dodging other mods' menu bars parked in the same spot. Motion is reported to
// SkinSelectorOverlay as frame-to-frame relative deltas, so the overlay can translate every
// group by the same screen-space offset regardless of which corner it's anchored to.
//
// Drag continues even when the pointer leaves the grip's rect: _GuiInput starts/stops the drag
// (it only fires while the cursor is over the control), and _Input carries the motion while the
// button is held so a fast drag doesn't "slip off" the handle. Double-click resets the position.
public partial class MenuDragHandle : Control
{
    private bool _dragging;

    public override void _GuiInput(InputEvent @event)
    {
        if (@event is not InputEventMouseButton mb || mb.ButtonIndex != MouseButton.Left) return;

        if (mb.Pressed)
        {
            if (mb.DoubleClick)
            {
                _dragging = false;
                AcceptEvent();
                Safe(SkinSelectorOverlay.ResetLayout);
                return;
            }
            _dragging = true;
            AcceptEvent();
        }
        else if (_dragging)
        {
            _dragging = false;
            AcceptEvent();
            Safe(SkinSelectorOverlay.OnDragEnd);
        }
    }

    // Runs before GUI event routing, so we get motion even once the cursor slides off the grip.
    public override void _Input(InputEvent @event)
    {
        if (!_dragging) return;

        if (@event is InputEventMouseMotion mm)
        {
            try { SkinSelectorOverlay.OnDragDelta(mm.Relative); }
            catch (Exception ex) { MainFile.Logger.Warn($"drag delta error: {ex.Message}"); }
            GetViewport().SetInputAsHandled();
        }
        else if (@event is InputEventMouseButton mb && mb.ButtonIndex == MouseButton.Left && !mb.Pressed)
        {
            // Button released off the grip — end the drag here since _GuiInput won't see it.
            _dragging = false;
            Safe(SkinSelectorOverlay.OnDragEnd);
        }
    }

    private static void Safe(Action action)
    {
        try { action(); }
        catch (Exception ex) { MainFile.Logger.Warn($"drag handle error: {ex.Message}"); }
    }
}
