using System.Runtime.InteropServices;

namespace Avion2.Overlay;

internal static class NativeMethods
{
    public const int GwlExStyle = -20;
    public const int WsExTransparent = 0x00000020;
    public const int WsExLayered = 0x00080000;
    public const int WsExNoActivate = 0x08000000;
    public const int WsExAppWindow = 0x00040000;
    public const int WmHotKey = 0x0312;

    public const uint ModAlt = 0x0001;
    public const uint ModControl = 0x0002;
    public const uint ModShift = 0x0004;
    public const uint ModNoRepeat = 0x4000;

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
    public static extern nint GetWindowLongPtr(nint hWnd, int index);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")]
    public static extern nint SetWindowLongPtr(nint hWnd, int index, nint value);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool RegisterHotKey(nint hWnd, int id, uint modifiers, uint virtualKey);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool UnregisterHotKey(nint hWnd, int id);

    /// <summary>
    /// Reads a hotkey such as "F9" or "Ctrl+Shift+M" into RegisterHotKey's modifiers and virtual key; false when it
    /// is empty ("off") or not a key.
    /// </summary>
    public static bool TryParseHotkey(string? text, out uint modifiers, out uint virtualKey)
    {
        modifiers = ModNoRepeat;
        virtualKey = 0;
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        foreach (string part in text.Split('+', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
        {
            switch (part.ToLowerInvariant())
            {
                case "ctrl" or "control":
                    modifiers |= ModControl;
                    break;
                case "shift":
                    modifiers |= ModShift;
                    break;
                case "alt":
                    modifiers |= ModAlt;
                    break;
                default:
                    if (virtualKey != 0 || !Enum.TryParse(part, ignoreCase: true, out System.Windows.Input.Key key) ||
                        key == System.Windows.Input.Key.None)
                    {
                        return false;
                    }

                    virtualKey = (uint)System.Windows.Input.KeyInterop.VirtualKeyFromKey(key);
                    break;
            }
        }

        return virtualKey != 0;
    }

    public static void SetExStyle(nint hwnd, int add, int remove)
    {
        nint style = GetWindowLongPtr(hwnd, GwlExStyle);
        style = (style | add) & ~(nint)remove;
        SetWindowLongPtr(hwnd, GwlExStyle, style);
    }
}
