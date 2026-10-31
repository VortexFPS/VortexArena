using System;
using System.Collections.Generic;

namespace VortexArena.Engine.Console;

/// <summary>
/// One held engine button: the C# successor to DarkPlaces' <c>kbutton_t</c> and its <c>KeyDown</c>/<c>KeyUp</c>
/// (cl_input.c:65-122). A button is down while ANY key that pressed it is still held, so two keys bound to
/// <c>+jump</c> keep jumping until both are released.
///
/// <list type="bullet">
///   <item><see cref="Press"/> records which key pressed it. A key already holding it is a repeat and changes
///         nothing (DP "repeating key").</item>
///   <item><see cref="Release"/> with a key forgets that key only. A key that never pressed it is ignored (DP
///         "key up without corresponding down", e.g. the release of a key whose press went to the menu).</item>
///   <item>A null key is DP's "typed manually at the console" (<c>k = -1</c>): <c>+jump</c> typed holds the button
///         like one more key, and <c>-jump</c> typed clears every key, "for unsticking".</item>
/// </list>
///
/// <para>One deliberate difference: DP's <c>down[2]</c> holds two keys and refuses a third ("Three keys down for a
/// button!"), so a third key neither presses the button nor keeps it down. Here any number of keys can hold it.</para>
///
/// <para><see cref="Presses"/> counts down edges. It stands in for DP's impulse-down bit (<c>state &amp; 2</c>),
/// which keeps a press that was released before the next move was sent in that move (cl_input.c:1801-1817 read
/// <c>state &amp; 3</c>; :2180 clears bit 2 after sending). A counter rather than a bit, so each reader keeps its own
/// "last seen" and no reader can leave a button stuck for another.</para>
/// </summary>
public sealed class KeyButton
{
    /// <summary>The key a console-typed <c>+command</c> holds the button with (DP's <c>k = -1</c>).</summary>
    private const string Typed = "\0typed";

    private readonly HashSet<string> _keys = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>True while at least one key (or a typed <c>+command</c>) holds the button.</summary>
    public bool Down => _keys.Count > 0;

    /// <summary>How many times the button has gone from up to down. Never reset, so a reader can compare it with
    /// the value it saw last time to learn whether a press happened in between.</summary>
    public uint Presses { get; private set; }

    /// <summary>DP <c>KeyDown</c>: <paramref name="key"/> presses the button; null = typed at the console.</summary>
    public void Press(string? key)
    {
        bool wasDown = Down;
        if (!_keys.Add(key ?? Typed))
            return; // repeating key
        if (!wasDown)
            Presses++;
    }

    /// <summary>DP <c>KeyUp</c>: <paramref name="key"/> lets go of the button; null = typed at the console, which
    /// clears every key holding it.</summary>
    public void Release(string? key)
    {
        if (key is null)
            _keys.Clear();
        else
            _keys.Remove(key);
    }

    /// <summary>Let go regardless of which keys hold it (DP <c>in_releaseall</c>, and a key-less <c>-command</c>).</summary>
    public void Clear() => _keys.Clear();
}
