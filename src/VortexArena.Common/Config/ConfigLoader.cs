using VortexArena.Common.Services;

namespace VortexArena.Common.Config;

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
/// chain (<c>balance-xonotic.cfg</c> → <c>bal-wep-xonotic.cfg</c>, <c>physicsX.cfg</c>, <c>physics.cfg</c>,
/// <c>turrets.cfg</c>, <c>vehicles.cfg</c>, <c>gametypes-server.cfg</c>, <c>mutators.cfg</c>, <c>monsters.cfg</c>,
/// <c>minigames.cfg</c>) without needing the client/menu config tree. The <c>readFile</c> delegate resolves a
/// config path (relative to the gamedir root, e.g. <c>"balance-xonotic.cfg"</c>) to its text, or null if absent.
///
/// <para>This used to say the chain execs <c>physicsBryan.cfg</c> — "the default physics, stock Xonotic +
/// <c>sv_step_upspeed_max 1</c>". That was **false**, and had been for a while: no such file existed, and
/// <c>xonotic-server.cfg:675</c> execs unmodified upstream <c>physicsX.cfg</c>. The divergence had been
/// applied by hand-editing both files and did not survive the content tree being re-pointed at a clean
/// upstream checkout. It is now <see cref="VortexCommonEntry"/> → <c>vortex-physics.cfg</c>, which upstream
/// does not own and an upstream refresh cannot revert.</para>
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
    /// The Vortex divergence layer's single entry point (restructure D8, §11). It execs the
    /// <c>vortex-*.cfg</c> files in turn, so adding a layer file later is a content change with no code
    /// change here.
    ///
    /// <para>Policy: the <c>xonotic-*.cfg</c> tree is upstream and is NEVER edited. Divergence is applied
    /// additively on top, exploiting the same last-wins <c>set</c> semantics this class already documents.
    /// The reason is concrete — the port's one config divergence was already lost exactly once the other
    /// way. <c>physicsBryan.cfg</c> was a hand-edited copy of <c>physicsX.cfg</c> plus a hand-edit to
    /// <c>xonotic-server.cfg</c> to exec it; re-pointing the content tree at a clean upstream checkout
    /// silently reverted both, and nothing failed loudly — the game simply ran stock physics while this
    /// file's own comment went on describing it as running ours.</para>
    ///
    /// <para>Must be exec'd LAST of the entry files (so it overrides) and BEFORE
    /// <c>Cvar_LockDefaults</c> (so its values become shipped defaults rather than being
    /// indistinguishable from player-typed ones — G15). A missing layer file is a no-op that increments
    /// <c>FilesMissing</c>, never a crash.</para>
    ///
    /// <para><c>vortex-binds.cfg</c> is deliberately NOT reachable from here: binds need the interpreter's
    /// <c>bind</c> sink to be registered first, so they are exec'd from the two call sites that re-exec
    /// <c>binds-xonotic.cfg</c>. See that file's header.</para>
    /// </summary>
    public const string VortexCommonEntry = "vortex-common.cfg";

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
    /// <remarks>
    /// <see cref="VortexCommonEntry"/> comes last so the Vortex layer overrides the upstream chain.
    /// </remarks>
    public static ConfigInterpreter LoadServerConfig(ICvarService cvars, Func<string, string?> readFile)
        => Load(cvars, readFile, ServerEntry, NotificationsEntry, VortexCommonEntry);
}
