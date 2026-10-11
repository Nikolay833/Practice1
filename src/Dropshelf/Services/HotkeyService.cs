using System;
using System.Collections.Generic;
using Dropshelf.Interop;

namespace Dropshelf.Services;

/// <summary>Global hotkeys (RegisterHotKey) dispatched through a <see cref="MessageWindow"/>.</summary>
internal sealed class HotkeyService : IDisposable
{
    public const int BasketId = 1;
    public const int ClipboardId = 2;

    private readonly MessageWindow _window;
    private readonly Dictionary<int, Action> _actions = new();

    public HotkeyService(MessageWindow window)
    {
        _window = window;
        _window.HotkeyPressed += OnHotkey;
    }

    /// <summary>Registers a hotkey. Returns false when another app already owns the combination.</summary>
    public bool Register(int id, uint modifiers, uint virtualKey, Action action)
    {
        Unregister(id);
        if (!NativeMethods.RegisterHotKey(_window.Handle, id, modifiers | NativeMethods.MOD_NOREPEAT, virtualKey))
        {
            Log.Error($"RegisterHotKey failed for id {id} (already in use?)");
            return false;
        }

        _actions[id] = action;
        return true;
    }

    public void Unregister(int id)
    {
        if (_actions.Remove(id))
        {
            NativeMethods.UnregisterHotKey(_window.Handle, id);
        }
    }

    private void OnHotkey(int id)
    {
        if (_actions.TryGetValue(id, out var action))
        {
            try
            {
                action();
            }
            catch (Exception ex)
            {
                Log.Error($"Hotkey {id} handler failed", ex);
            }
        }
    }

    public void Dispose()
    {
        _window.HotkeyPressed -= OnHotkey;
        foreach (int id in new List<int>(_actions.Keys))
        {
            Unregister(id);
        }
    }
}
