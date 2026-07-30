using XonoticGodot.Common.Services;

namespace XonoticGodot.Server;

/// <summary>
/// Temporary-cvar bookkeeping — the C# successor to <c>cvar_settemp</c> / <c>cvar_settemp_restore</c>
/// (common/util.qc). A "settemp" remembers a cvar's original value the first time it is overridden, applies
/// the new value, and can later restore every overridden cvar to what it was. Xonotic uses this for per-map
/// and per-campaign-level overrides (mapinfo settemp ACL, the <c>nospectators</c> / <c>setbots</c> commands,
/// campaign mutator sets, the <c>settemp</c> console command) so the changes revert when the map ends.
///
/// Faithful to QC: the original value is captured only once per cvar (a second <c>Set</c> of the same cvar
/// does not overwrite the saved original), and <see cref="Restore"/> writes every saved original back and
/// clears the table. Setting a cvar that does not exist is a no-op (QC logs an error and returns).
/// </summary>
public static class SettempCvars
{
    // name -> the value the cvar held BEFORE the first settemp override (QC the saved_cvar_value.message).
    private static readonly Dictionary<string, string> _saved = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>How many cvars currently have a pending settemp override (QC the g_saved_cvars count).</summary>
    public static int Count => _saved.Count;

    /// <summary>True if <paramref name="name"/> currently has a saved original awaiting restore.</summary>
    public static bool IsOverridden(string name) => _saved.ContainsKey(name);

    /// <summary>
    /// QC <c>cvar_settemp(name, value)</c>: remember the cvar's current value (once), then set the new value.
    /// Returns false (and does nothing) if there is no cvar service or the cvar has never been registered —
    /// matching QC's "cvar doesn't exist → log + return 0". Registering the original happens only the first
    /// time, so repeated settemps of the same cvar all restore to the earliest captured value.
    /// </summary>
    public static bool Set(string name, string value)
    {
        if (Api.Services is null || string.IsNullOrEmpty(name))
            return false;

        if (!_saved.ContainsKey(name))
            _saved[name] = Api.Cvars.GetString(name); // capture the original exactly once
        Api.Cvars.Set(name, value);
        return true;
    }

    /// <summary>Convenience: settemp a numeric cvar (QC <c>cvar_settemp(name, ftos(value))</c>).</summary>
    public static bool Set(string name, float value)
        => Set(name, value.ToString(System.Globalization.CultureInfo.InvariantCulture));

    /// <summary>
    /// QC <c>cvar_settemp_restore()</c>: write every saved original back to its cvar and clear the table.
    /// Returns the number of cvars restored. Safe to call when nothing is overridden (returns 0).
    /// </summary>
    public static int Restore()
    {
        if (Api.Services is null)
        {
            int n0 = _saved.Count;
            _saved.Clear();
            return n0;
        }
        int n = 0;
        foreach (var kv in _saved)
        {
            Api.Cvars.Set(kv.Key, kv.Value);
            n++;
        }
        _saved.Clear();
        return n;
    }

    /// <summary>Drop all saved originals WITHOUT restoring (test teardown / a fresh world). Not a QC concept.</summary>
    public static void Clear() => _saved.Clear();
}
