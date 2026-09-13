using System.Globalization;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.UI.Input.KeyboardAndMouse;
using Windows.Win32.UI.WindowsAndMessaging;

namespace Dudu.App.System;

[Flags]
public enum HotkeyModifiers : uint
{
    None = 0,
    Ctrl = 0x0002,
    Alt = 0x0001,
    Shift = 0x0004,
    Win = 0x0008,
}

public sealed record HotkeyGesture
{
    private HotkeyGesture(HotkeyModifiers modifiers, uint key)
    {
        Modifiers = modifiers;
        Key = key;
    }

    public HotkeyModifiers Modifiers { get; }

    public uint Key { get; }

    public static HotkeyGesture Default { get; } = Create(
        HotkeyModifiers.Ctrl | HotkeyModifiers.Alt,
        0x44);

    public static HotkeyGesture Parse(string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        var tokens = value.Split('+', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        if (tokens.Length < 2)
        {
            throw new FormatException("A global hotkey requires one or more modifiers and one key.");
        }

        var modifiers = HotkeyModifiers.None;
        uint key = 0;
        var keyCount = 0;
        foreach (var token in tokens)
        {
            if (TryParseModifier(token, out var modifier))
            {
                if ((modifiers & modifier) != 0)
                {
                    throw new FormatException("A modifier may only appear once.");
                }

                modifiers |= modifier;
                continue;
            }

            if (++keyCount != 1 || !TryParseKey(token, out key))
            {
                throw new FormatException("A global hotkey requires exactly one non-modifier key.");
            }
        }

        if (modifiers == HotkeyModifiers.None || keyCount != 1)
        {
            throw new FormatException("A global hotkey requires one or more modifiers and one key.");
        }

        return Create(modifiers, key);
    }

    public bool IsValid => Modifiers != HotkeyModifiers.None && Key != 0;

    public override string ToString()
    {
        var parts = new List<string>(5);
        if ((Modifiers & HotkeyModifiers.Ctrl) != 0) parts.Add("Ctrl");
        if ((Modifiers & HotkeyModifiers.Alt) != 0) parts.Add("Alt");
        if ((Modifiers & HotkeyModifiers.Shift) != 0) parts.Add("Shift");
        if ((Modifiers & HotkeyModifiers.Win) != 0) parts.Add("Win");
        parts.Add(Key switch
        {
            >= 0x30 and <= 0x39 => ((char)Key).ToString(CultureInfo.InvariantCulture),
            >= 0x41 and <= 0x5A => ((char)Key).ToString(CultureInfo.InvariantCulture),
            >= 0x70 and <= 0x87 => $"F{Key - 0x6F}",
            0x1B => "Esc",
            0x20 => "Space",
            0x09 => "Tab",
            0x0D => "Enter",
            0x25 => "Left",
            0x26 => "Up",
            0x27 => "Right",
            0x28 => "Down",
            0x2D => "Insert",
            0x2E => "Delete",
            0x24 => "Home",
            0x23 => "End",
            0x21 => "PageUp",
            0x22 => "PageDown",
            _ => $"VK_{Key:X2}",
        });
        return string.Join('+', parts);
    }

    private static HotkeyGesture Create(HotkeyModifiers modifiers, uint key)
    {
        if (modifiers == HotkeyModifiers.None || key == 0)
        {
            throw new ArgumentException("A global hotkey requires one or more modifiers and one key.");
        }

        return new HotkeyGesture(modifiers, key);
    }

    private static bool TryParseModifier(string token, out HotkeyModifiers modifier)
    {
        modifier = token.ToUpperInvariant() switch
        {
            "CTRL" or "CONTROL" => HotkeyModifiers.Ctrl,
            "ALT" => HotkeyModifiers.Alt,
            "SHIFT" => HotkeyModifiers.Shift,
            "WIN" or "WINDOWS" or "META" => HotkeyModifiers.Win,
            _ => HotkeyModifiers.None,
        };
        return modifier != HotkeyModifiers.None;
    }

    private static bool TryParseKey(string token, out uint key)
    {
        key = 0;
        if (token.Length == 1 && char.IsLetterOrDigit(token[0]))
        {
            key = char.ToUpperInvariant(token[0]);
            return true;
        }

        if (token.Length > 1
            && token[0] is 'F' or 'f'
            && uint.TryParse(token[1..], NumberStyles.None, CultureInfo.InvariantCulture, out var function)
            && function is >= 1 and <= 24)
        {
            key = 0x6F + function;
            return true;
        }

        var namedKey = token.ToUpperInvariant() switch
        {
            "ESC" or "ESCAPE" => 0x1Bu,
            "SPACE" => 0x20u,
            "TAB" => 0x09u,
            "ENTER" or "RETURN" => 0x0Du,
            "LEFT" => 0x25u,
            "UP" => 0x26u,
            "RIGHT" => 0x27u,
            "DOWN" => 0x28u,
            "INSERT" => 0x2Du,
            "DELETE" or "DEL" => 0x2Eu,
            "HOME" => 0x24u,
            "END" => 0x23u,
            "PAGEUP" or "PGUP" => 0x21u,
            "PAGEDOWN" or "PGDN" => 0x22u,
            _ => 0u,
        };
        if (namedKey != 0)
        {
            key = namedKey;
            return true;
        }

        return false;
    }
}

public sealed class HotkeyConflictException(string message) : InvalidOperationException(message);

public interface IGlobalHotkeyNativeApi
{
    bool Register(int id, HotkeyModifiers modifiers, uint key);
    bool Unregister(int id);

    bool Register(nint ownerWindow, int id, HotkeyModifiers modifiers, uint key) =>
        Register(id, modifiers, key);

    bool Unregister(nint ownerWindow, int id) => Unregister(id);
}

public sealed class GlobalHotkeyService : IDisposable
{
    public const uint WmHotkey = 0x0312;
    public const int DefaultId = 0xD0D;

    private readonly IGlobalHotkeyNativeApi _native;
    private readonly object _gate = new();
    private HotkeyGesture _currentGesture = HotkeyGesture.Default;
    private nint _ownerWindow;
    private int _registeredId;
    private int _nextId = DefaultId + 1;
    private bool _disposed;

    public GlobalHotkeyService(IGlobalHotkeyNativeApi? native = null)
    {
        _native = native ?? new WindowsGlobalHotkeyNativeApi();
    }

    public HotkeyGesture CurrentGesture
    {
        get
        {
            lock (_gate) return _currentGesture;
        }
    }

    public event EventHandler? Triggered;

    public nint OwnerWindow
    {
        get
        {
            lock (_gate) return _ownerWindow;
        }
    }

    public void AttachOwnerWindow(nint ownerWindow)
    {
        if (ownerWindow == 0) throw new ArgumentOutOfRangeException(nameof(ownerWindow));
        lock (_gate)
        {
            ThrowIfDisposed();
            if (_ownerWindow == ownerWindow) return;

            if (_registeredId == 0)
            {
                _ownerWindow = ownerWindow;
                return;
            }

            var previousWindow = _ownerWindow;
            var requestedId = _nextId++;
            if (!_native.Register(ownerWindow, requestedId, _currentGesture.Modifiers, _currentGesture.Key))
            {
                throw new HotkeyConflictException("aiyo couldnt move the global hotkey to the owner window");
            }

            if (!_native.Unregister(previousWindow, _registeredId))
            {
                if (!_native.Unregister(ownerWindow, requestedId))
                {
                    throw new HotkeyConflictException(
                        "aiyo couldnt restore the previous hotkey or undo the new one");
                }

                throw new HotkeyConflictException("aiyo couldnt move the previous global hotkey");
            }

            _ownerWindow = ownerWindow;
            _registeredId = requestedId;
        }
    }

    public void SetGesture(HotkeyGesture gesture)
    {
        ArgumentNullException.ThrowIfNull(gesture);
        lock (_gate)
        {
            ThrowIfDisposed();
            if (!gesture.IsValid)
            {
                throw new FormatException("A global hotkey requires one or more modifiers and one key.");
            }
            if (_registeredId != 0 && gesture == _currentGesture)
            {
                return;
            }

            var requestedId = _registeredId == 0 ? DefaultId : _nextId++;
            if (!_native.Register(_ownerWindow, requestedId, gesture.Modifiers, gesture.Key))
            {
                throw new HotkeyConflictException($"aiyo {gesture} is already taken by another shortcut");
            }

            if (_registeredId != 0 && !_native.Unregister(_ownerWindow, _registeredId))
            {
                if (!_native.Unregister(_ownerWindow, requestedId))
                {
                    throw new HotkeyConflictException(
                        "aiyo couldnt replace the previous hotkey or undo the new one");
                }

                throw new HotkeyConflictException("aiyo couldnt replace the previous global hotkey");
            }

            _registeredId = requestedId;
            _currentGesture = gesture;
        }
    }

    public bool HandleMessage(uint message, nint hotkeyId)
    {
        EventHandler? triggered;
        lock (_gate)
        {
            if (!_disposed && message == WmHotkey && hotkeyId == _registeredId && _registeredId != 0)
            {
                triggered = Triggered;
            }
            else
            {
                return false;
            }
        }

        try
        {
            triggered?.Invoke(this, EventArgs.Empty);
        }
        catch
        {
            // A consumer callback must not tear down the native message loop.
        }

        return true;
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            if (_registeredId != 0)
            {
                _native.Unregister(_ownerWindow, _registeredId);
                _registeredId = 0;
            }
        }
    }

    private void ThrowIfDisposed()
    {
        if (_disposed) throw new ObjectDisposedException(nameof(GlobalHotkeyService));
    }
}

internal sealed unsafe class WindowsGlobalHotkeyNativeApi : IGlobalHotkeyNativeApi
{
    public bool Register(int id, HotkeyModifiers modifiers, uint key) =>
        Register(0, id, modifiers, key);

    public bool Unregister(int id) => Unregister(0, id);

    public bool Register(nint ownerWindow, int id, HotkeyModifiers modifiers, uint key) =>
        PInvoke.RegisterHotKey(
            new HWND((void*)ownerWindow),
            id,
            (HOT_KEY_MODIFIERS)(uint)modifiers,
            key);

    public bool Unregister(nint ownerWindow, int id) =>
        PInvoke.UnregisterHotKey(new HWND((void*)ownerWindow), id);
}
