// Port of Base/darkplaces/keys.c keybindings[][], key_bmap / key_bmap2, Key_SetBinding, Key_GetBind,
// Key_FindKeysForCommand, Key_GetBindMap, Key_SetBindMap, Key_WriteBindings, and the console commands
// Key_Bind_f, Key_Unbind_f, Key_Unbindall_f, Key_In_Bind_f, Key_In_Unbind_f, Key_In_Bindmap_f,
// Key_BindList_f; with cmd.c Cmd_QuoteString as Key_WriteBindings uses it.
using System.Globalization;
using System.Text;
using VortexArena.Common.Config;
using VortexArena.Legacy.Csqc;

namespace VortexArena.Legacy.Menu;

/// <summary>
/// DarkPlaces' key bindings: eight "bind maps", each a table from key number to console command, and
/// the pair of maps (foreground, fallback) a key press is looked up in.
///
/// This is the table Xonotic's Input settings edit (setkeybind, findkeysforcommand), the one its
/// default configuration fills (binds-xonotic.cfg is a file of <c>bind</c> commands) and the one a
/// legacy session started from the Xonotic menu takes its keys from. It is separate from the player's
/// native bind table on purpose: Xonotic's commands (<c>+fire</c>, <c>weapon_group_1</c>,
/// <c>messagemode2</c>) mean nothing to the native client, and the native ones mean nothing to a
/// Xonotic server's program.
/// </summary>
public sealed class MenuKeyBindings
{
    public const int MaxBindMaps = CsqcKeys.MaxBindMaps;
    public const int MaxKeys = CsqcKeys.MaxKeys;

    /// <summary>MAX_INPUTLINE: the longest command a key can hold.</summary>
    public const int MaxBindingLength = 16384;

    // keybindings[MAX_BINDMAPS][MAX_KEYS] is 352,256 pointers in the C; a stock configuration binds
    // about a hundred keys, all in map 0.
    private readonly SortedDictionary<int, string>[] _maps = new SortedDictionary<int, string>[MaxBindMaps];
    private int _foreground, _background = 1;   // key_bmap = 0, key_bmap2 = 1 (keys.c)

    public MenuKeyBindings()
    {
        for (int i = 0; i < MaxBindMaps; i++) _maps[i] = new SortedDictionary<int, string>();
    }

    /// <summary>Raised after any binding or the active maps change.</summary>
    public event Action? Changed;

    /// <summary>Key_SetBinding. An empty command removes the binding ("make "" binds be removed").</summary>
    public bool SetBinding(int key, int bindMap, string command)
    {
        if (key < 0 || key >= MaxKeys) return false;
        if (bindMap < 0 || bindMap >= MaxBindMaps) return false;
        command ??= "";
        if (command.Length >= MaxBindingLength) command = command[..(MaxBindingLength - 1)];
        if (command.Length == 0) _maps[bindMap].Remove(key);
        else _maps[bindMap][key] = command;
        Changed?.Invoke();
        return true;
    }

    /// <summary>Key_GetBind: the command on a key in one map, or (map -1) in the active pair. Null if unbound.</summary>
    public string? GetBind(int key, int bindMap)
    {
        if (key < 0 || key >= MaxKeys) return null;
        if (bindMap >= MaxBindMaps) return null;
        if (bindMap >= 0) return _maps[bindMap].GetValueOrDefault(key);
        return _maps[_foreground].GetValueOrDefault(key) ?? _maps[_background].GetValueOrDefault(key);
    }

    /// <summary>
    /// Key_FindKeysForCommand: the first <paramref name="keys"/>.Length key numbers bound to exactly this
    /// command, in ascending order, the rest -1.
    /// </summary>
    public void FindKeysForCommand(string command, Span<int> keys, int bindMap)
    {
        keys.Fill(-1);
        if (bindMap >= MaxBindMaps || keys.Length == 0) return;
        int count = 0;
        if (bindMap >= 0)
        {
            foreach ((int key, string bound) in _maps[bindMap])
            {
                if (!string.Equals(bound, command, StringComparison.Ordinal)) continue;
                keys[count++] = key;
                if (count == keys.Length) return;
            }
            return;
        }
        // The active pair: the C walks every key number and asks Key_GetBind, so a key bound in the
        // foreground map hides what the fallback map has on it. Merge the two sorted tables the same way.
        using SortedDictionary<int, string>.Enumerator fg = _maps[_foreground].GetEnumerator(), bg = _maps[_background].GetEnumerator();
        bool haveFg = fg.MoveNext(), haveBg = _foreground != _background && bg.MoveNext();
        while (haveFg || haveBg)
        {
            int key;
            string bound;
            if (haveFg && (!haveBg || fg.Current.Key <= bg.Current.Key))
            {
                key = fg.Current.Key;
                bound = fg.Current.Value;
                if (haveBg && bg.Current.Key == key) haveBg = bg.MoveNext();
                haveFg = fg.MoveNext();
            }
            else
            {
                key = bg.Current.Key;
                bound = bg.Current.Value;
                haveBg = bg.MoveNext();
            }
            if (!string.Equals(bound, command, StringComparison.Ordinal)) continue;
            keys[count++] = key;
            if (count == keys.Length) return;
        }
    }

    /// <summary>Key_GetBindMap.</summary>
    public (int Foreground, int Background) BindMap => (_foreground, _background);

    /// <summary>Key_SetBindMap: a negative number leaves that one as it is.</summary>
    public bool SetBindMap(int foreground, int background)
    {
        if (foreground >= MaxBindMaps || background >= MaxBindMaps) return false;
        if (foreground >= 0) _foreground = foreground;
        if (background >= 0) _background = background;
        Changed?.Invoke();
        return true;
    }

    /// <summary>Key_Unbindall_f.</summary>
    public void UnbindAll()
    {
        foreach (SortedDictionary<int, string> map in _maps) map.Clear();
        Changed?.Invoke();
    }

    /// <summary>Bound keys of one map, ascending.</summary>
    public IEnumerable<KeyValuePair<int, string>> Bindings(int bindMap) =>
        (uint)bindMap < MaxBindMaps ? _maps[bindMap] : Array.Empty<KeyValuePair<int, string>>();

    /// <summary>
    /// Key_WriteBindings: "unbindall", then one <c>bind</c> line per key of map 0 and one
    /// <c>in_bind</c> line per key of the others. The command is quoted with Cmd_QuoteString(..., "\"\\"):
    /// only the quote and the backslash are escaped, "$" is not ("cvars are not expanded inside bind").
    /// </summary>
    public void Write(TextWriter file)
    {
        file.Write("unbindall\n");
        for (int map = 0; map < MaxBindMaps; map++)
        {
            foreach ((int key, string command) in _maps[map])
            {
                string name = CsqcKeys.KeynumToString(key);
                string quoted = Quote(command, "\"\\");
                if (map == 0) file.Write($"bind {name} \"{quoted}\"\n");
                else file.Write(string.Create(CultureInfo.InvariantCulture, $"in_bind {map} {name} \"{quoted}\"\n"));
            }
        }
    }

    /// <summary>cmd.c Cmd_QuoteString: a backslash before each character of <paramref name="set"/>, and "$" doubled if it is in the set.</summary>
    public static string Quote(string text, string set)
    {
        bool quoteDollar = set.Contains('$'), quoteQuote = set.Contains('"'), quoteBackslash = set.Contains('\\');
        StringBuilder result = new(text.Length + 8);
        foreach (char c in text)
        {
            if (c == '"' && quoteQuote) result.Append("\\\"");
            else if (c == '\\' && quoteBackslash) result.Append("\\\\");
            else if (c == '$' && quoteDollar) result.Append("$$");
            else result.Append(c);
        }
        return result.ToString();
    }

    /// <summary>
    /// Registers bind, unbind, unbindall, in_bind, in_unbind, in_bindmap and bindlist. Each handler first
    /// asks <paramref name="allowed"/>: a command buffer that carries a server's text must not be able to
    /// rebind the player's keys, and the owner of the buffer is what knows whose text is running.
    /// </summary>
    public void RegisterCommands(ConfigInterpreter interpreter, Action<string> print, Func<bool>? allowed = null)
    {
        ArgumentNullException.ThrowIfNull(interpreter);
        bool Allowed(string command)
        {
            if (allowed is null || allowed()) return true;
            print($"\"{command}\" from the server was not run: key bindings are the player's\n");
            return false;
        }

        interpreter.RegisterCommand("bind", argv =>
        {
            if (!Allowed("bind")) return;
            if (argv.Count != 2 && argv.Count != 3)
            {
                print("bind <key> [command] : attach a command to a key\n");
                return;
            }
            int key = CsqcKeys.StringToKeynum(argv[1]);
            if (key == -1 || key >= MaxKeys)
            {
                print($"\"{argv[1]}\" isn't a valid key\n");
                return;
            }
            if (argv.Count == 2)
            {
                print(_maps[0].TryGetValue(key, out string? bound) ? $"\"{argv[1]}\" = \"{bound}\"\n" : $"\"{argv[1]}\" is not bound\n");
                return;
            }
            SetBinding(key, 0, argv[2]);
        }, "attach a command to a key");

        interpreter.RegisterCommand("unbind", argv =>
        {
            if (!Allowed("unbind")) return;
            if (argv.Count != 2)
            {
                print("unbind <key> : remove commands from a key\n");
                return;
            }
            int key = CsqcKeys.StringToKeynum(argv[1]);
            if (key == -1)
            {
                print($"\"{argv[1]}\" isn't a valid key\n");
                return;
            }
            SetBinding(key, 0, "");
        }, "removes a command on the specified key in bindmap 0");

        interpreter.RegisterCommand("unbindall", _ => { if (Allowed("unbindall")) UnbindAll(); }, "removes all commands from all keys in all bindmaps (leaving only shift-escape and escape)");

        interpreter.RegisterCommand("in_bind", argv =>
        {
            if (!Allowed("in_bind")) return;
            if (argv.Count != 3 && argv.Count != 4)
            {
                print("in_bind <bindmap> <key> [command] : attach a command to a key\n");
                return;
            }
            if (!TryBindMap(argv[1], out int map))
            {
                print($"{argv[1]} isn't a valid bindmap\n");
                return;
            }
            int key = CsqcKeys.StringToKeynum(argv[2]);
            if (key == -1 || key >= MaxKeys)
            {
                print($"\"{argv[2]}\" isn't a valid key\n");
                return;
            }
            if (argv.Count == 3)
            {
                print(_maps[map].TryGetValue(key, out string? bound) ? $"\"{argv[2]}\" = \"{bound}\"\n" : $"\"{argv[2]}\" is not bound\n");
                return;
            }
            SetBinding(key, map, argv[3]);
        }, "binds a command to the specified key in the selected bindmap");

        interpreter.RegisterCommand("in_unbind", argv =>
        {
            if (!Allowed("in_unbind")) return;
            if (argv.Count != 3)
            {
                print("in_unbind <bindmap> <key> : remove commands from a key\n");
                return;
            }
            if (!TryBindMap(argv[1], out int map))
            {
                print($"{argv[1]} isn't a valid bindmap\n");
                return;
            }
            int key = CsqcKeys.StringToKeynum(argv[2]);
            if (key == -1)
            {
                print($"\"{argv[2]}\" isn't a valid key\n");
                return;
            }
            SetBinding(key, map, "");
        }, "removes command on the specified key in the selected bindmap");

        interpreter.RegisterCommand("in_bindmap", argv =>
        {
            if (!Allowed("in_bindmap")) return;
            if (argv.Count != 3)
            {
                print("in_bindmap <bindmap> <fallback>: set current bindmap and fallback\n");
                return;
            }
            if (!TryBindMap(argv[1], out int first))
            {
                print($"{argv[1]} isn't a valid bindmap\n");
                return;
            }
            if (!TryBindMap(argv[2], out int second))
            {
                print($"{argv[2]} isn't a valid bindmap\n");
                return;
            }
            SetBindMap(first, second);
        }, "selects active foreground and background (used only if a key is not bound in the foreground) bindmaps for typing");

        interpreter.RegisterCommand("bindlist", _ =>
        {
            for (int map = 0; map < MaxBindMaps; map++)
                foreach ((int key, string command) in _maps[map])
                    print(map == 0 ? $"{CsqcKeys.KeynumToString(key)} \"{command}\"\n" : $"{CsqcKeys.KeynumToString(key)} in bindmap {map} \"{command}\"\n");
        }, "bindlist: displays bound keys for all bindmaps, or bindlist <bindmap>: displays bound keys for selected bindmap");
    }

    // strtol(text, &end, 0) with the whole text consumed, in [0, MAX_BINDMAPS).
    private static bool TryBindMap(string text, out int map) =>
        int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out map) && map < MaxBindMaps;
}
