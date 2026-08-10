using System.Runtime.InteropServices;
using PcHost.Protocol;

namespace PcHost.Input;

/// <summary>
/// Translates parsed InputEvent packets (shared-protocol/PROTOCOL.md) into real Windows input
/// via user32.dll's SendInput.
/// </summary>
public static class InputInjector
{
    public static void Inject(InputEvent evt)
    {
        switch (evt.EventType)
        {
            case InputEventType.MouseMove:
                SendMouse(MOUSEEVENTF_MOVE, (int)MathF.Round(evt.Dx), (int)MathF.Round(evt.Dy));
                break;

            case InputEventType.Scroll:
                if (evt.Dy != 0)
                {
                    SendMouse(MOUSEEVENTF_WHEEL, 0, 0, mouseData: (uint)(int)MathF.Round(evt.Dy * WHEEL_DELTA));
                }
                if (evt.Dx != 0)
                {
                    SendMouse(MOUSEEVENTF_HWHEEL, 0, 0, mouseData: (uint)(int)MathF.Round(evt.Dx * WHEEL_DELTA));
                }
                break;

            case InputEventType.LeftClick:
                SendInputs(
                    MouseInput(MOUSEEVENTF_LEFTDOWN, 0, 0),
                    MouseInput(MOUSEEVENTF_LEFTUP, 0, 0));
                break;

            case InputEventType.RightClick:
                SendInputs(
                    MouseInput(MOUSEEVENTF_RIGHTDOWN, 0, 0),
                    MouseInput(MOUSEEVENTF_RIGHTUP, 0, 0));
                break;

            case InputEventType.LeftDown:
                SendMouse(MOUSEEVENTF_LEFTDOWN, 0, 0);
                break;

            case InputEventType.LeftUp:
                SendMouse(MOUSEEVENTF_LEFTUP, 0, 0);
                break;

            case InputEventType.KeyDown:
                SendKey(evt.KeyCode, keyUp: false);
                break;

            case InputEventType.KeyUp:
                SendKey(evt.KeyCode, keyUp: true);
                break;
        }
    }

    private static void SendMouse(uint flags, int dx, int dy, uint mouseData = 0)
        => SendInputs(MouseInput(flags, dx, dy, mouseData));

    private static void SendKey(ushort vk, bool keyUp)
    {
        var input = new INPUT
        {
            type = INPUT_KEYBOARD,
            U = new InputUnion
            {
                ki = new KEYBDINPUT
                {
                    wVk = vk,
                    wScan = 0,
                    dwFlags = keyUp ? KEYEVENTF_KEYUP : 0,
                    time = 0,
                    dwExtraInfo = IntPtr.Zero,
                },
            },
        };
        SendInputs(input);
    }

    private static INPUT MouseInput(uint flags, int dx, int dy, uint mouseData = 0) => new()
    {
        type = INPUT_MOUSE,
        U = new InputUnion
        {
            mi = new MOUSEINPUT
            {
                dx = dx,
                dy = dy,
                mouseData = mouseData,
                dwFlags = flags,
                time = 0,
                dwExtraInfo = IntPtr.Zero,
            },
        },
    };

    private static void SendInputs(params INPUT[] inputs)
    {
        uint sent = SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<INPUT>());
        if (sent != inputs.Length)
        {
            int error = Marshal.GetLastWin32Error();
            Console.WriteLine($"[input] SendInput sent {sent}/{inputs.Length} events (Win32 error {error})");
        }
    }

    #region P/Invoke

    private const uint INPUT_MOUSE = 0;
    private const uint INPUT_KEYBOARD = 1;

    private const uint MOUSEEVENTF_MOVE = 0x0001;
    private const uint MOUSEEVENTF_LEFTDOWN = 0x0002;
    private const uint MOUSEEVENTF_LEFTUP = 0x0004;
    private const uint MOUSEEVENTF_RIGHTDOWN = 0x0008;
    private const uint MOUSEEVENTF_RIGHTUP = 0x0010;
    private const uint MOUSEEVENTF_WHEEL = 0x0800;
    private const uint MOUSEEVENTF_HWHEEL = 0x1000;
    private const int WHEEL_DELTA = 120;

    private const uint KEYEVENTF_KEYUP = 0x0002;

    [StructLayout(LayoutKind.Sequential)]
    private struct INPUT
    {
        public uint type;
        public InputUnion U;
    }

    [StructLayout(LayoutKind.Explicit)]
    private struct InputUnion
    {
        [FieldOffset(0)] public MOUSEINPUT mi;
        [FieldOffset(0)] public KEYBDINPUT ki;
        [FieldOffset(0)] public HARDWAREINPUT hi;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MOUSEINPUT
    {
        public int dx;
        public int dy;
        public uint mouseData;
        public uint dwFlags;
        public uint time;
        public IntPtr dwExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct KEYBDINPUT
    {
        public ushort wVk;
        public ushort wScan;
        public uint dwFlags;
        public uint time;
        public IntPtr dwExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct HARDWAREINPUT
    {
        public uint uMsg;
        public ushort wParamL;
        public ushort wParamH;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint SendInput(uint nInputs, [In] INPUT[] pInputs, int cbSize);

    #endregion
}
