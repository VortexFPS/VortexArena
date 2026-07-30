using System;

namespace VortexArena.Game.Console;

/// <summary>
/// One global flag: is the in-game console open? The C# stand-in for DP's <c>key_dest == key_console</c>
/// gate that suppresses gameplay input while the console has focus.
///
/// <para>Why a static flag and not just <c>SetInputAsHandled()</c>: the play path
/// (<see cref="VortexArena.Game.Net.NetGame"/>) samples movement by <em>polling</em>
/// (<c>Input.IsPhysicalKeyPressed</c>), and polled OS key state is NOT suppressed by consuming an
/// <c>InputEvent</c>. So WASD/fire would keep firing under the open console unless the sampler explicitly
/// checks this flag. It also lets the bind layer drop all held buttons (DP <c>in_releaseall</c>) the instant
/// the console opens, via <see cref="Opened"/>.</para>
/// </summary>
public static class ConsoleState
{
    private static bool _isOpen;

    /// <summary>True while the console overlay is showing. Setting it to true fires <see cref="Opened"/>.</summary>
    public static bool IsOpen
    {
        get => _isOpen;
        set
        {
            if (_isOpen == value)
                return;
            _isOpen = value;
            if (value)
                Opened?.Invoke();
        }
    }

    /// <summary>Raised when the console transitions from closed to open — the bind layer releases all held
    /// buttons here so a key held at open-time doesn't stick down once the console closes.</summary>
    public static event Action? Opened;
}
