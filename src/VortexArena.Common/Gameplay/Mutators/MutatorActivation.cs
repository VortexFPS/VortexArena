using XonoticGodot.Common.Framework;

namespace XonoticGodot.Common.Gameplay;

/// <summary>
/// The mutator activation loop — the C# successor to QuakeC's <c>Mutator_Add</c> / <c>Mutator_Remove</c> and
/// the <c>STATIC_INIT_LATE(Mutators)</c> that runs them over the enabled mutators
/// (common/mutators/base.qh:237-307).
///
/// In QC, once all mutators have registered, <c>STATIC_INIT_LATE</c> does
/// <c>FOREACH(Mutators, _MUTATOR_IS_ENABLED(it), Mutator_Add(it))</c>: it walks every registered mutator and,
/// for each whose <c>mutatorcheck()</c> predicate passes, calls <c>Mutator_Add</c> — which (guarded by the
/// per-mutator <c>m_added</c> flag) runs the mutator's hook-registration function and subscribes its
/// callbacks to the hook chains. Without this loop the mutators are registered but inert: their
/// <see cref="MutatorBase.Hook"/> is never called, so none of their hook handlers ever run during a match.
///
/// This is that loop. <see cref="Apply"/> reconciles every registered mutator's subscription state against
/// its live <see cref="MutatorBase.IsEnabled"/> (cvar) predicate: enabled-but-not-added mutators are
/// <see cref="Add"/>ed, added-but-no-longer-enabled mutators are <see cref="Remove"/>d. It is idempotent (the
/// <see cref="MutatorBase.Added"/> guard mirrors QC's <c>m_added</c>), so a host may call it once at boot and
/// again whenever the mutator cvars change (a ruleset vote, a campaign level) to re-converge — the QC
/// equivalent of toggling <c>g_*</c> and re-running the add/remove path.
/// </summary>
public static class MutatorActivation
{
    /// <summary>
    /// Host seam for QC <c>cvar_settemp(name, value)</c> from a mutator's <c>MUTATOR_ONADD</c> (e.g. Random
    /// Gravity settemps <c>sv_gravity</c> so the original is restored at match end). The settemp restore-stack
    /// (<c>cvar_settemp_restore</c>) is owned by the server host (XonoticGodot.Server.SettempCvars), which the
    /// Godot-free Common layer can't reference, so the host wires this to <c>SettempCvars.Set</c> at boot. When
    /// unwired (headless tests) it falls back to a plain cvar set — the override still applies, only the
    /// match-end restore is skipped (which has no live cvar host to restore to anyway).
    /// </summary>
    public static Action<string, string>? SettempCvarHandler;

    /// <summary>
    /// QC <c>cvar_settemp(name, value)</c>: route a mutator ONADD settemp through the host's restore stack via
    /// <see cref="SettempCvarHandler"/>, or fall back to a plain cvar set when no host is wired.
    /// </summary>
    public static void SettempCvar(string name, string value)
    {
        if (SettempCvarHandler is { } h) h(name, value);
        else if (XonoticGodot.Common.Services.Api.Services is not null) XonoticGodot.Common.Services.Api.Cvars.Set(name, value);
    }

    /// <summary>
    /// QC <c>Mutator_Add(mut)</c> (base.qh:237): subscribe a mutator's hooks if not already subscribed.
    /// Idempotent via the <see cref="MutatorBase.Added"/> guard (QC <c>if (mut.m_added) return true;</c>).
    /// Returns true if the mutator is now active.
    /// </summary>
    public static bool Add(MutatorBase mut)
    {
        if (mut.Added)
            return true; // already added (QC m_added short-circuit)
        mut.Added = true;
        mut.Hook(); // QC mutatorfunc(MUTATOR_ADDING) → the MUTATOR_HOOK CallbackChain_Add path
        return true;
    }

    /// <summary>
    /// QC <c>Mutator_Remove(mut)</c> (base.qh:260): unsubscribe a mutator's hooks. No-op (with the same
    /// "removing not-added mutator" guard QC warns on) if it was never added.
    /// </summary>
    public static void Remove(MutatorBase mut)
    {
        if (!mut.Added)
            return; // QC backtraces "removing not-added mutator" then returns
        mut.Added = false;
        mut.Unhook(); // QC mutatorfunc(MUTATOR_REMOVING) → the MUTATOR_HOOK CallbackChain_Remove path
    }

    /// <summary>
    /// QC <c>STATIC_INIT_LATE(Mutators): FOREACH(Mutators, _MUTATOR_IS_ENABLED(it), Mutator_Add(it))</c>
    /// (base.qh:305), extended to also <see cref="Remove"/> any mutator that is currently added but whose
    /// enable predicate no longer holds — so re-running this after the mutator cvars change converges to the
    /// correct active set (QC achieves the same by toggling <c>g_*</c> and re-running add/remove).
    ///
    /// Call once after the registries are built and the config is loaded (so each mutator's
    /// <see cref="MutatorBase.IsEnabled"/> cvar read is meaningful), e.g. from the server boot right after the
    /// gametype is activated and before the map's entities (which a mutator may filter) are spawned.
    /// </summary>
    public static void Apply()
    {
        foreach (MutatorBase mut in Mutators.All)
        {
            if (mut.IsEnabled)
                Add(mut);
            else
                Remove(mut);
        }
    }

    /// <summary>
    /// Unsubscribe every currently-added mutator (test/teardown support, and the symmetric counterpart to
    /// <see cref="Apply"/> when a world is torn down). Mirrors removing all mutators at match end so the
    /// global hook chains don't leak handlers into the next match.
    /// </summary>
    public static void DeactivateAll()
    {
        foreach (MutatorBase mut in Mutators.All)
            Remove(mut);
    }

    /// <summary>
    /// QC <c>MUTATOR_CALLHOOK(BuildMutatorsString, s)</c> (server/gamelog.qc:50): run the BuildMutatorsString
    /// hook chain — each currently-active (<see cref="MutatorBase.Added"/>) mutator appends its colon-delimited
    /// machine token (e.g. <c>":Vampire"</c>) to the accumulator. Returns the full string. Only added mutators
    /// contribute, mirroring QC where the hook only fires for mutators whose handler was subscribed.
    /// </summary>
    public static string BuildMutatorsString(string s)
    {
        foreach (MutatorBase mut in Mutators.All)
            if (mut.Added)
                s = mut.BuildMutatorsString(s);
        return s;
    }

    /// <summary>
    /// QC <c>MUTATOR_CALLHOOK(BuildMutatorsPrettyString, "")</c> (server/client.qc:1107): run the
    /// BuildMutatorsPrettyString hook chain — each active mutator appends its <c>", &lt;Pretty&gt;"</c> token.
    /// The caller strips the leading <c>", "</c> after the chain (QC <c>substring(s, 2, strlen(s) - 2)</c>).
    /// </summary>
    public static string BuildMutatorsPrettyString(string s)
    {
        foreach (MutatorBase mut in Mutators.All)
            if (mut.Added)
                s = mut.BuildMutatorsPrettyString(s);
        return s;
    }

    /// <summary>
    /// QC <c>MUTATOR_CALLHOOK(LogDeath_AppendItemCodes, player, s)</c> (server/damage.qc:95, inside
    /// <c>AppendItemcodes</c>): run the LogDeath item-code hook chain — each active mutator appends its
    /// per-player death-log item code(s) (powerups "S"/"I", ctf "F", …) to the accumulator. Returns the
    /// full string. Only added mutators contribute (mirrors the QC hook firing only for subscribed handlers).
    /// </summary>
    public static string LogDeathAppendItemCodes(Entity player, string s)
    {
        foreach (MutatorBase mut in Mutators.All)
            if (mut.Added)
                s = mut.LogDeathAppendItemCodes(player, s);
        return s;
    }

    /// <summary>
    /// QC <c>MUTATOR_CALLHOOK(SetModname, modname)</c> (server/world.qc:1090): run the SetModname hook chain —
    /// the first active mutator that returns <c>overridden=true</c> sets the modname and the chain stops (QC
    /// CBC_ORDER_ANY early-exit). Returns the final resolved modname string.
    /// </summary>
    public static string SetModname(string name)
    {
        foreach (MutatorBase mut in Mutators.All)
        {
            if (!mut.Added) continue;
            var (newName, overridden) = mut.SetModname(name);
            if (overridden)
                return newName;
        }
        return name;
    }
}
