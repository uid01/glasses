namespace PcHost.Protocol;

/// <summary>
/// InputEventType enum values, per shared-protocol/PROTOCOL.md "InputEvent" section.
/// </summary>
public enum InputEventType : byte
{
    MouseMove = 0,
    Scroll = 1,
    LeftClick = 2,
    RightClick = 3,
    LeftDown = 4,
    LeftUp = 5,
    KeyDown = 6,
    KeyUp = 7,
}
