using XonoticGodot.Common.Services;

namespace XonoticGodot.Common.Config;

/// <summary>
/// High-level entry points for loading the stock Xonotic configuration into an <see cref="ICvarService"/>.
/// Wraps <see cref="ConfigInterpreter"/> with the right entry file(s) and a couple of pre-seeded aliases so a
/// host (the Godot client, the dedicated server, or a test) can populate authentic cvar values in one call:
///
/// <code>
///   var interp = ConfigLoader.LoadServerConfig(cvars, path => vfs.Exists(path) ? vfs.ReadText(path) : null);
///   Log($"loaded {interp.CvarsAssigned} cvars from {interp.FilesExecuted} cfg files");
/// </code>
///
/// The default entry is <c>xonotic-server.cfg</c>, which <c>exec</c>s the entire authoritative gameplay-cvar
/// chain (<c>balance-xonotic.cfg</c> → <c>bal-wep-xonotic.cfg</c>, <c>physicsBryan.cfg</c> [the default physics —
/// stock Xonotic + sv_step_upspeed_max 1], <c>physics.cfg</c>,
/// <c>turrets.cfg</c>, <c>vehicles.cfg</c>, <c>gametypes-server.cfg</c>, <c>mutators.cfg</c>, <c>monsters.cfg</c>,
/// <c>minigames.cfg</c>) without needing the client/menu config tree. The <c>readFile</c> delegate resolves a
/// config path (relative to the gamedir root, e.g. <c>"balance-xonotic.cfg"</c>) to its text, or null if absent.
/// </summary>
public static class ConfigLoader
{
    /// <summary>The server-side gameplay entry config (execs balance/physics/gametypes/mutators/… in turn).</summary>
    public const string ServerEntry = "xonotic-server.cfg";

    /// <summary>The full client/common root (also pulls in the client/HUD/menu tree — heavier; rarely needed headless).</summary>
    public const string CommonEntry = "xonotic-common.cfg";

    /// <summary>The notification cvar table (centerprint/announcer toggles), exec'd separately by the common root.</summary>
    public const string NotificationsEntry = "notifications.cfg";

    /// <summary>
    /// Build an interpreter, pre-seed the conditional-exec aliases (so any stray <c>if_client</c>/<c>if_dedicated</c>
    /// directive runs its arguments rather than being misread), and execute each entry file in order. Later files
    /// override earlier ones (DP <c>set</c> semantics) — pass a balance variant after the server entry to mod it.
    /// </summary>
    public static ConfigInterpreter Load(ICvarService cvars, Func<string, string?> readFile, params string[] entryFiles)
        => Load(cvars, readFile, archiveHook: null, entryFiles);

    /// <summary>
    /// <see cref="Load(ICvarService, Func{string, string?}, string[])"/> with a <c>seta</c> archive callback.
    /// In DP the shipped cfg tree itself decides which cvars are archiveable: <c>seta name value</c> sets
    /// CVAR_SAVE on the cvar (<c>Cvar_SetA_f</c> → <c>Cvar_Get(…, CVAR_SAVE, …)</c>) while plain <c>set</c> does
    /// not, and <c>Cvar_WriteVariables</c> later persists only the archived-and-changed ones. Pass the store's
    /// mark-archived here (the client does: <c>MenuState.Boot</c> → <c>CvarService.MarkArchived</c>) so that
    /// provenance survives the port; a store that is never saved (a private world store) can pass null.
    /// </summary>
    public static ConfigInterpreter Load(ICvarService cvars, Func<string, string?> readFile,
        Action<string>? archiveHook, params string[] entryFiles)
    {
        var interp = new ConfigInterpreter(cvars, readFile) { CvarArchiveHook = archiveHook };
        // `${* asis}` = "run my arguments as-is" — the passthrough form used by stock configs when these aren't
        // redefined to a no-op by the dedicated-server detection (which lives in the client/common tree we skip).
        interp.DefineAlias("if_client", "${* asis}");
        interp.DefineAlias("if_dedicated", "${* asis}");

        foreach (string file in entryFiles)
            interp.ExecuteFile(file);
        return interp;
    }

    /// <summary>
    /// Load the authoritative server gameplay configuration (<see cref="ServerEntry"/> + the notification table).
    /// This is the one call a headless/listen server makes after mounting assets to get real balance/physics/
    /// gametype/mutator/monster cvar values instead of the hand-curated defaults.
    /// </summary>
    public static ConfigInterpreter LoadServerConfig(ICvarService cvars, Func<string, string?> readFile)
        => Load(cvars, readFile, ServerEntry, NotificationsEntry);
}
