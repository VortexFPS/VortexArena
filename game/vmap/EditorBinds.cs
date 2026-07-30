using System.Collections.Generic;
using VortexArena.Common.Config;
using VortexArena.Common.Diagnostics;

namespace VortexArena.Game.Vmap;

/// <summary>
/// The editor's own key table (design doc §11.9) — a bind layer that exists ONLY while free-flying in an
/// editor session.
///
/// It is separate from the game's <c>BindTable</c> on purpose, and the reason is the requirement itself: keys
/// 0-9 belong to the editor <i>exclusively</i> while editing. Doing that through the shared table would mean
/// either overwriting whatever the player has bound to the digits (destroying their config the first time they
/// open a map) or losing the fight to it (so a player with chat macros on the number row cannot use the tools).
/// A separate layer, consulted first and only in EDIT, does not have that trade: the player's binds are
/// untouched and come straight back the moment they drop into PLAYTEST.
///
/// It is a real bind table rather than a hardcoded switch because the HUD displays these keys. §11.6's rule is
/// that a panel resolves every key it shows from whatever is actually bound, so that rebinding updates the
/// readout instead of leaving it confidently wrong; a switch statement would force the panel back to
/// hardcoding "[3]" and lying the moment anything moved.
/// </summary>
public static class EditorBinds
{
    /// <summary>
    /// The default digit layout. Tools on 1-6 in the order a mapper meets them, then the three view/state
    /// toggles, with the BSP comparison on 0 where it has always been.
    /// </summary>
    private static readonly (string Key, string Command)[] Defaults =
    {
        ("1", "editor_tool Select"),
        ("2", "editor_tool Brush"),
        ("3", "editor_tool Face"),
        ("4", "editor_tool Edge"),
        ("5", "editor_tool Vertex"),
        ("6", "editor_tool Patch"),
        ("7", "editor_mode"),
        ("8", "editor_ortho"),
        ("9", "editor_grid_snap"),
        ("g", "editor_grid"),
        ("0", "editor_show_bsp"),
    };

    private static readonly Dictionary<string, string> Table = new(System.StringComparer.OrdinalIgnoreCase);

    static EditorBinds() => Reset();

    /// <summary>Restore the shipped layout.</summary>
    public static void Reset()
    {
        Table.Clear();
        foreach ((string key, string command) in Defaults)
            Table[key] = command;
    }

    /// <summary>The command bound to <paramref name="key"/>, or empty.</summary>
    public static string Command(string key)
        => Table.TryGetValue(key, out string? c) ? c : "";

    /// <summary>The command bound to a digit, or empty.</summary>
    public static string CommandForDigit(int digit)
        => digit is >= 0 and <= 9 ? Command(digit.ToString(System.Globalization.CultureInfo.InvariantCulture)) : "";

    /// <summary>
    /// The key bound to <paramref name="command"/> in brackets, or <c>--</c> when nothing is. Matching is
    /// exact, so the string passed here must be exactly what is bound — the same contract as the game's
    /// <c>BindTable.CommandKey</c>, and for the same reason: a HUD that guesses is worse than one that admits
    /// it does not know.
    /// </summary>
    public static string KeyLabel(string command)
    {
        foreach ((string key, string bound) in Table)
            if (string.Equals(bound, command, System.StringComparison.OrdinalIgnoreCase))
                return $"[{key}]";
        return "[--]";
    }

    /// <summary>Bind a key, or unbind it with an empty command.</summary>
    public static void Set(string key, string command)
    {
        if (string.IsNullOrEmpty(key))
            return;
        if (string.IsNullOrEmpty(command))
            Table.Remove(key);
        else
            Table[key] = command;
    }

    /// <summary>Every binding, for the console listing.</summary>
    public static IEnumerable<KeyValuePair<string, string>> All => Table;

    /// <summary>
    /// Register <c>editor_bind</c>. Bare lists the layer; one argument reports that key; two rebinds it.
    /// </summary>
    public static void RegisterCommands(ConfigInterpreter interp)
    {
        System.ArgumentNullException.ThrowIfNull(interp);

        interp.RegisterCommand("editor_bind", argv =>
        {
            if (argv.Count <= 1)
            {
                Log.Info("editor binds (active only while free-flying in the editor):");
                foreach ((string key, string command) in Table)
                    Log.Info($"  {key,-3} {command}");
                Log.Help("usage: editor_bind <key> [command]   ·   editor_bind reset");
                return;
            }

            if (string.Equals(argv[1], "reset", System.StringComparison.OrdinalIgnoreCase))
            {
                Reset();
                Log.Info("editor binds reset to defaults");
                return;
            }

            if (argv.Count == 2)
            {
                string bound = Command(argv[1]);
                Log.Info(bound.Length > 0 ? $"{argv[1]} = {bound}" : $"{argv[1]} is not bound in the editor layer");
                return;
            }

            // Join the tail so `editor_bind 3 editor_tool Face` works without quoting.
            string cmd = string.Join(' ', argv, 2, argv.Count - 2);
            Set(argv[1], cmd);
            Log.Info($"editor_bind: {argv[1]} = {cmd}");
        });
    }
}
