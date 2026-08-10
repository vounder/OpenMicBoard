using System.Runtime.InteropServices;
using OpenMicBoard.Core;

namespace OpenMicBoard.App.Services;

/// <summary>
/// Registers explicit, non-repeating Windows hotkeys and routes WM_HOTKEY to pad IDs.
/// Registration is process-local and never persists OS-wide state.
/// </summary>
public sealed class GlobalHotkeyService : IDisposable
{
    private const int GwlWndProc = -4;
    private const uint WmHotkey = 0x0312;
    private const uint ModNoRepeat = 0x4000;
    private readonly Dictionary<int, Guid> _registrations = [];
    private readonly WindowProcedure _windowProcedure;
    private nint _windowHandle;
    private nint _previousWindowProcedure;
    private int _nextId = 0x4F4D;
    private bool _disposed;

    public GlobalHotkeyService()
    {
        _windowProcedure = WindowMessage;
    }

    public event EventHandler<Guid>? Pressed;

    public void Attach(nint windowHandle)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_windowHandle != 0)
        {
            return;
        }

        _windowHandle = windowHandle;
        _previousWindowProcedure = SetWindowLongPtr(
            _windowHandle,
            GwlWndProc,
            Marshal.GetFunctionPointerForDelegate(_windowProcedure));
        if (_previousWindowProcedure == 0)
        {
            throw new InvalidOperationException(
                "The window message hook for global hotkeys could not be installed.");
        }
    }

    public bool TryRegister(string chord, Guid padId, out string? error)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_windowHandle == 0)
        {
            error = "The application window is not ready.";
            return false;
        }

        if (!HotkeyChordParser.TryParse(chord, out var parsed, out error))
        {
            return false;
        }

        var id = _nextId++;
        if (!RegisterHotKey(
                _windowHandle,
                id,
                parsed.Modifiers | ModNoRepeat,
                parsed.VirtualKey))
        {
            error = "This shortcut is already used by Windows or another application.";
            return false;
        }

        _registrations.Add(id, padId);
        error = null;
        return true;
    }

    public void Clear()
    {
        if (_windowHandle == 0)
        {
            return;
        }

        foreach (var id in _registrations.Keys)
        {
            UnregisterHotKey(_windowHandle, id);
        }

        _registrations.Clear();
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        Clear();
        if (_windowHandle != 0 && _previousWindowProcedure != 0)
        {
            SetWindowLongPtr(_windowHandle, GwlWndProc, _previousWindowProcedure);
        }

        _windowHandle = 0;
        _previousWindowProcedure = 0;
        _disposed = true;
    }

    private nint WindowMessage(nint hwnd, uint message, nuint wParam, nint lParam)
    {
        if (message == WmHotkey &&
            _registrations.TryGetValue(unchecked((int)wParam), out var padId))
        {
            Pressed?.Invoke(this, padId);
            return 0;
        }

        return CallWindowProc(_previousWindowProcedure, hwnd, message, wParam, lParam);
    }

    private static nint SetWindowLongPtr(nint hwnd, int index, nint newValue) =>
        Environment.Is64BitProcess
            ? SetWindowLongPtr64(hwnd, index, newValue)
            : new nint(SetWindowLong32(hwnd, index, newValue.ToInt32()));

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate nint WindowProcedure(
        nint hwnd,
        uint message,
        nuint wParam,
        nint lParam);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW", SetLastError = true)]
    private static extern nint SetWindowLongPtr64(nint hwnd, int index, nint newValue);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongW", SetLastError = true)]
    private static extern int SetWindowLong32(nint hwnd, int index, int newValue);

    [DllImport("user32.dll")]
    private static extern nint CallWindowProc(
        nint previous,
        nint hwnd,
        uint message,
        nuint wParam,
        nint lParam);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool RegisterHotKey(
        nint hwnd,
        int id,
        uint modifiers,
        uint virtualKey);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UnregisterHotKey(nint hwnd, int id);
}
