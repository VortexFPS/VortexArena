// Port of Base/.../qcsrc/common/mutators/mutator/status_effects/* (status_effects.qh networking:
//      ENT_CLIENT_STATUSEFFECTS, StatusEffects_Write/Send, STATUSEFFECT_FLAG_PERSISTENT/ACTIVE,
//      StatusEffects_update; sv_status_effects.qc m_apply/m_remove/m_tick dirty-marking).
using System;
using System.Collections.Generic;
using XonoticGodot.Common.Framework;
using XonoticGodot.Common.Gameplay.Damage;
using static XonoticGodot.Common.Gameplay.Sounds;
using XonoticGodot.Common.Services;

namespace XonoticGodot.Common.Gameplay;

/// <summary>
/// The status-effect networking flags — the C# successor to QC's <c>STATUSEFFECT_FLAG_*</c>
/// (status_effects/all.qh:20-24). Stored per active effect and networked alongside its timer so the client
/// can drive the burning/frozen overlays.
/// </summary>
[System.Flags]
public enum StatusEffectFlags
{
    /// <summary>QC STATUSEFFECT_FLAG_ACTIVE (BIT 0): the effect is currently applied (set on m_apply).</summary>
    Active = 1 << 0,

    /// <summary>
    /// QC STATUSEFFECT_FLAG_PERSISTENT (BIT 1): the effect is being granted passively (it does not time out
    /// while persistent). Burning/frozen overlays read this so they persist on the client. Set by a def's
    /// <c>m_persistent</c> in <c>m_tick</c>; a persistent effect skips removal-on-timeout.
    /// </summary>
    Persistent = 1 << 1,
}

/// <summary>The removal cause passed to a status-effect removal (QC <c>STATUSEFFECT_REMOVE_*</c>, all.qh:26-31).</summary>
public enum StatusEffectRemoval
{
    /// <summary>QC STATUSEFFECT_REMOVE_NORMAL: regular removal (plays the removal sound unless persistent).</summary>
    Normal = 0,
    /// <summary>QC STATUSEFFECT_REMOVE_TIMEOUT: the effect's timer elapsed.</summary>
    Timeout = 1,
    /// <summary>QC STATUSEFFECT_REMOVE_CLEAR: forced removal with no additional mechanics.</summary>
    Clear = 2,
}

/// <summary>
/// A status effect kind (QC common/mutators/mutator/status_effects + buffs/powerups). The full QC
/// <c>StatusEffects</c> registry: the core debuffs (frozen/burning/spawnshield/stunned/superweapon/webbed),
/// the powerups (strength/shield/speed/invisibility — <c>Buff</c> derives from <c>StatusEffect</c>), and the
/// buff set. Registered into <see cref="StatusEffectsCatalog"/> via RegisterAll (self-registering).
/// </summary>
public sealed class StatusEffectDef : IRegistered
{
    public int RegistryId { get; set; }
    public string Name = "";
    public string RegistryName => Name;
    public bool IsBuff;            // a buff (BuffsMutator-offerable) vs a core effect/powerup
    public bool Hidden;            // QC m_hidden — no HUD element (spawnshield/stunned/webbed)
    public float Lifetime;         // QC m_lifetime — default duration cap (0 = none)
    public string? Model;          // buff model / drop model

    /// <summary>QC <c>m_name</c> (all.qh:39): the HUD/menu display label (null for the hidden debuffs whose
    /// QC <c>m_name</c> is #if 0'd out — spawnshield/stunned/frozen/burning; the visible superweapon carries one).
    /// The live HUD still reconstructs label/icon/color from PowerupMeta, so this is carried for data-parity.</summary>
    public string? DisplayName;
    /// <summary>QC <c>m_icon</c> (all.qh:40): the HUD progress-bar icon name (null where the QC attrib is #if 0'd).</summary>
    public string? Icon;
    /// <summary>QC <c>m_color</c> (all.qh:41, default '1 1 1'): the HUD progress-bar tint (RGB 0..1).
    /// For a buff this is also the pickup item's <c>color</c>/<c>glowmod</c> (sv_buffs.qc:305-306).</summary>
    public (float R, float G, float B) Color = (1f, 1f, 1f);

    /// <summary>QC <c>Buff.m_skin</c> (buffs.qh:25, default 0): the relic.md3 skin index the buff pickup item
    /// (and carrier glow model) wears, one per buff type (sv_buffs.qc:307 <c>this.skin = buff.m_skin</c>). Each
    /// buff's <c>.qh</c> overrides it; non-buff effects leave it 0.</summary>
    public int Skin;

    /// <summary>
    /// QC <c>m_sound_rm</c> (all.qh:44): the sound played on a NORMAL removal of an active, non-persistent
    /// effect (sv_status_effects.qc:46-47). The registered port sound-catalog name (resolved null-safe via
    /// <see cref="SoundSystem.PlayOn(Entity,string)"/> — a no-op if the sound isn't registered yet).
    /// Null/empty = no removal sound.
    /// </summary>
    public string? RemovalSound;

    /// <summary>
    /// QC <c>m_persistent(this, actor)</c> (all.qh:55; burning.qc:9-12 lava, superweapons.qc:4-7 unlimited):
    /// recomputed every tick — when it returns true the effect carries the PERSISTENT flag and never times
    /// out. Null = the base default (always false, all.qh:55).
    /// </summary>
    public Func<Entity, bool>? PersistentCheck;

    /// <summary>
    /// QC per-effect <c>m_tick</c> body (burning/spawnshield/stunned set/clear EF_* and self-extinguish):
    /// invoked every tick while the effect is active, AFTER the PERSISTENT recompute and BEFORE the timeout
    /// check. Returns true to request a NORMAL self-removal this tick (burning in water/while frozen,
    /// stunned while frozen — sv_status_effects per-effect m_tick). Null = no per-effect tick.
    /// </summary>
    public Func<Entity, ActiveStatusEffect, bool>? OnTick;

    /// <summary>
    /// QC per-effect <c>m_remove</c> override (spawnshield.qc:5 clears EF_ADDITIVE|EF_FULLBRIGHT, stunned.qc:5
    /// clears EF_SHOCK, frozen/burning clear their own .effects bit, and the powerups' strength.qc:5-13 /
    /// shield.qc / speed.qc / invisibility.qc m_remove fire the CENTER_POWERDOWN_X print on a TIMEOUT removal +
    /// stopsound(CH_TRIGGER_SINGLE) on ANY removal): invoked on ANY removal (NORMAL/TIMEOUT/CLEAR) with the
    /// removal cause so the handler can gate the timeout-only mechanics (the powerdown centerprint) — Base runs
    /// the SUPER m_remove after the per-effect override, passing removal_type through. Null = no per-effect
    /// removal mechanics.
    /// </summary>
    public Action<Entity, ActiveStatusEffect, StatusEffectRemoval>? OnRemove;

    /// <summary>
    /// QC per-effect <c>m_apply</c> override that runs only on the <c>!wasactive</c> transition (i.e. the first
    /// time the effect becomes active — sv_status_effects.qc m_apply / frozen.qc:46-53): invoked once when the
    /// effect is freshly added (NOT on a refresh of an already-active effect). Frozen uses this to drop the
    /// actor's grappling hooks (frozen.qc:50 RemoveGrapplingHooks(actor)). Null = no on-apply mechanics.
    /// </summary>
    public Action<Entity>? OnApply;

    public StatusEffectDef(string name, bool isBuff = false) { Name = name; IsBuff = isBuff; }
}

/// <summary>One active effect instance on an entity (with expiry + optional source/strength).</summary>
public struct ActiveStatusEffect
{
    public int DefId;
    public float ExpireTime;       // engine time when it ends (<=0 => permanent)
    public float Strength;         // effect magnitude (e.g. burn dps, buff level)
    public Entity? Source;         // who applied it (for kill credit)
    /// <summary>QC <c>statuseffect_flags[id]</c>: the ACTIVE/PERSISTENT bitmap networked with the timer.</summary>
    public StatusEffectFlags Flags;
}

/// <summary>The status-effect system: apply/remove/query + a per-frame tick (burn damage, expiry, buff timers).</summary>
public static class StatusEffectsCatalog
{
    public static IReadOnlyList<StatusEffectDef> All => Registry<StatusEffectDef>.All;
    public static StatusEffectDef? ByName(string name) => Registry<StatusEffectDef>.ByName(name);
    public static int Count => Registry<StatusEffectDef>.Count;

    // Well-known effects (resolved after RegisterAll).
    public static StatusEffectDef? Frozen => ByName("frozen");
    public static StatusEffectDef? Burning => ByName("burning");
    public static StatusEffectDef? SpawnShield => ByName("spawnshield");
    public static StatusEffectDef? Stunned => ByName("stunned");
    public static StatusEffectDef? Superweapon => ByName("superweapon");
    public static StatusEffectDef? Webbed => ByName("webbed");

    /// <summary>
    /// Mirror of the QC <c>StatusEffects</c> registry (REGISTER_STATUSEFFECT across status_effect/, powerups/,
    /// buffs/, monsters/spider). The buffs carry <see cref="StatusEffectDef.IsBuff"/> so BuffsMutator offers
    /// only those; powerups + core debuffs do not. Names match the XonoticGodot consumers (ItemPickupRules powerup
    /// timers use "shield"/"superweapon"; BuffsMutator + DeathTypes use the "buff_" prefix; MonsterFramework
    /// uses "webbed"/"shield"/"spawnshield").
    /// </summary>
    public static void RegisterAll()
    {
        void R(StatusEffectDef d) => Registry<StatusEffectDef>.Register(d);

        // --- core debuffs / status (status_effect/*.qh) ---
        // STATUSEFFECT_Frozen (frozen.qh): m_color '0 0.62 1', hidden. m_apply (frozen.qc:50) drops the actor's
        // grappling hooks on the !wasactive transition — both freezetag freeze and nade-ice freeze route
        // through this shared apply, so wiring it here mirrors Base's single chokepoint (no per-call-site dup).
        R(new StatusEffectDef("frozen")
        {
            Hidden = true,
            Color = (0f, 0.62f, 1f),
            OnApply = Hook.RemoveGrapplingHooks,
        });
        // STATUSEFFECT_Burning (burning.qh): EF_FLAME flame + Fire_ApplyDamage. m_persistent = burning while
        // standing in lava (g_balance_contents_playerdamage_lava_burn, default 0). m_tick self-extinguishes in
        // non-lava water or while STAT(FROZEN). m_sound_rm = the steam-burst hiss (burning.qh SND_Burning_Remove).
        R(new StatusEffectDef("burning")
        {
            DisplayName = "Burning",
            Icon = "buff_inferno",
            Hidden = true,
            Lifetime = 10f,
            Color = (1f, 0.62f, 0f),   // QC m_color '1 0.62 0'
            RemovalSound = "BURNING_REMOVE",
            PersistentCheck = BurningPersistent,
            OnTick = BurningTick,
            OnRemove = BurningRemove,
        });
        // STATUSEFFECT_SpawnShield (spawnshield.qh): hidden, 10s cap — post-spawn damage protection.
        // m_tick sets EF_ADDITIVE|EF_FULLBRIGHT shimmer once time >= game_starttime (spawnshield.qc:11-13);
        // m_remove clears those bits (spawnshield.qc:5). Driven via the networked Entity.Effects field.
        R(new StatusEffectDef("spawnshield")
        {
            Hidden = true,
            Lifetime = 10f,
            Color = (0.36f, 1f, 0.07f),   // QC m_color '0.36 1 0.07'
            OnTick = SpawnShieldTick,
            OnRemove = SpawnShieldRemove,
        });
        // STATUSEFFECT_Stunned (stunned.qh): hidden, 10s cap — shockwave/onslaught stun. m_tick self-removes
        // while STAT(FROZEN) else sets the EF_SHOCK aura (stunned.qc:16-25); m_remove clears EF_SHOCK
        // (stunned.qc:5). m_sound_rm = the spark snap (ons_spark1).
        R(new StatusEffectDef("stunned")
        {
            DisplayName = "Stunned",
            Icon = "buff_disability",
            Hidden = true,
            Lifetime = 10f,
            Color = (0.67f, 0.84f, 1f),   // QC m_color '0.67 0.84 1'
            RemovalSound = "STUNNED_REMOVE",
            OnTick = StunnedTick,
            OnRemove = StunnedRemove,
        });
        // STATUSEFFECT_Superweapon (superweapons.qh): superweapon ammo window (QC netname "superweapons";
        // XonoticGodot consumers use the singular "superweapon"). m_persistent = IT_UNLIMITED_SUPERWEAPONS
        // (so the networked PERSISTENT bit is now set instead of faking it with a 999s timer);
        // m_sound_rm = POWEROFF on the countdown lapse (server/client.qc play_countdown).
        R(new StatusEffectDef("superweapon")
        {
            DisplayName = "Superweapons",   // QC m_name _("Superweapons") (superweapons.qh)
            Icon = "superweapons",          // QC m_icon "superweapons"
            RemovalSound = "POWEROFF",
            PersistentCheck = SuperweaponPersistent,
        });
        // STATUSEFFECT_Webbed (monsters/monster/spider.qh): spider-web movement slow.
        R(new StatusEffectDef("webbed") { Hidden = true });

        // --- powerups (mutators/mutator/powerups/powerup/*.qh — Buff:StatusEffect, but not BuffsMutator buffs) ---
        // NOTE: the strength/shield role-glow (strength.qc:31 EF_BLUE|EF_ADDITIVE|EF_FULLBRIGHT, shield.qc:31
        // EF_RED|EF_ADDITIVE|EF_FULLBRIGHT m_tick + the m_remove:14 clear) is already driven faithfully + live by
        // PowerupsMutator.PlayerPreThink (always enabled, self-gated on StatusEffectsCatalog.Has), ORing the bits
        // into the networked Entity.Effects each frame; the CSQC model hooks (CsqcModelEffects.Apply) light them.
        // So these defs intentionally carry no OnTick glow — the bits would otherwise be set in two places.
        // QC powerups (strength.qh / shield.qh / speed.qh / invisibility.qh) m_name/m_icon/m_color:
        // QC powerup m_remove (strength.qc:5-13 and the parallel shield/speed/invisibility): on a TIMEOUT removal
        // CENTER_POWERDOWN_X is printed to the player, and on ANY removal stopsound(actor, CH_TRIGGER_SINGLE) cuts
        // the long powerup pickup fanfare. PowerupsMutator.PlayerPreThink drives the rising-edge pickup notif +
        // the glow/alpha/countdown; the FALLING-edge powerdown lives here (in m_remove) so it sees the real
        // removal cause and only prints on TIMEOUT (a drop-on-death / +use drop is NORMAL and must stay silent).
        R(new StatusEffectDef("strength") { DisplayName = "Strength", Icon = "strength", Color = (0f, 0f, 1f), OnRemove = PowerupRemove });
        R(new StatusEffectDef("shield") { DisplayName = "Shield", Icon = "shield", Color = (1f, 0f, 1f), OnRemove = PowerupRemove });      // QC ShieldStatusEffect netname "invincible"; consumers use "shield"
        R(new StatusEffectDef("speed") { DisplayName = "Speed", Icon = "buff_speed", Color = (0.1f, 1f, 0.84f), OnRemove = PowerupRemove });
        R(new StatusEffectDef("invisibility") { DisplayName = "Invisibility", Icon = "buff_invisible", Color = (0.5f, 0.5f, 1f), OnRemove = PowerupRemove });

        // --- buffs (mutators/mutator/buffs/buff/*.qh): the exact QC set, offered by BuffsMutator ---
        // m_skin / m_color taken straight from each buff's .qh ATTRIB; the pickup item wears the skin and is
        // tinted by the color (sv_buffs.qc:305-307 this.color/glowmod/skin = buff.m_color/m_color/m_skin).
        R(new StatusEffectDef("buff_ammo", isBuff: true)       { Skin = 3,  Color = (0.29f, 0.37f, 1f) });
        R(new StatusEffectDef("buff_bash", isBuff: true)       { Skin = 5,  Color = (1f, 0.39f, 0f) });
        R(new StatusEffectDef("buff_disability", isBuff: true) { Skin = 7,  Color = (0.94f, 0.3f, 1f) });
        R(new StatusEffectDef("buff_flight", isBuff: true)     { Skin = 11, Color = (0.23f, 0.44f, 1f) });
        R(new StatusEffectDef("buff_inferno", isBuff: true)    { Skin = 16, Color = (1f, 0.62f, 0f) });
        R(new StatusEffectDef("buff_jump", isBuff: true)       { Skin = 10, Color = (0.24f, 0.78f, 1f) });
        R(new StatusEffectDef("buff_luck", isBuff: true)       { Skin = 19, Color = (0.17f, 0.85f, 0.12f) });
        R(new StatusEffectDef("buff_magnet", isBuff: true)     { Skin = 18, Color = (1f, 0.95f, 0.18f) });
        R(new StatusEffectDef("buff_medic", isBuff: true)      { Skin = 1,  Color = (1f, 0.12f, 0f) });
        R(new StatusEffectDef("buff_resistance", isBuff: true) { Skin = 0,  Color = (0.36f, 1f, 0.07f) });
        R(new StatusEffectDef("buff_swapper", isBuff: true)    { Skin = 17, Color = (0.63f, 0.36f, 1f) });
        R(new StatusEffectDef("buff_vampire", isBuff: true)    { Skin = 2,  Color = (1f, 0f, 0.24f) });
        R(new StatusEffectDef("buff_vengeance", isBuff: true)  { Skin = 15, Color = (1f, 0.23f, 0.61f) });

        Registry<StatusEffectDef>.Sort();
    }

    // ============================================================================================
    //  Per-effect m_persistent / m_tick bodies (QC status_effect/<effect>.qc)
    // ============================================================================================

    private const int ContentLava = (int)Contents.Lava;

    /// <summary>QC Burning <c>m_persistent</c> (burning.qc:9-12): keep burning while standing in lava (only
    /// when <c>g_balance_contents_playerdamage_lava_burn</c> is enabled — default 0, so normally false).</summary>
    private static bool BurningPersistent(Entity e)
        => CvarBool("g_balance_contents_playerdamage_lava_burn")
           && e.WaterLevel != 0 && e.WaterType == ContentLava;

    /// <summary>QC Burning <c>m_tick</c> (burning.qc:13-23): self-extinguish in non-lava water or while
    /// frozen, otherwise apply the per-tick fire damage (<see cref="FireApplyDamage"/>) and OR the networked
    /// EF_FLAME bit (burning.qc:21) so the client renders the flame via the .effects flag (like
    /// spawnshield/stunned), not a per-frame client particle. Returns true to request the NORMAL self-removal
    /// (the OnRemove handler clears EF_FLAME).</summary>
    private static bool BurningTick(Entity e, ActiveStatusEffect s)
    {
        if (IsStatusFrozen(e) || (e.WaterLevel != 0 && e.WaterType != ContentLava))
            return true;   // m_remove(NORMAL) — BurningRemove clears EF_FLAME
        FireApplyDamage(e, s);
        e.Effects |= EfFlame;   // QC actor.effects |= EF_FLAME (burning.qc:21)
        return false;
    }

    /// <summary>
    /// QC powerup <c>m_remove</c> (strength.qc:4-14 and the parallel shield/speed/invisibility m_remove): on a
    /// TIMEOUT removal of a real player print <c>CENTER_POWERDOWN_X</c> to the owner (the INFO_POWERDOWN_X
    /// broadcast is commented out in Base, so only the CENTER fires); on ANY removal cut the long pickup fanfare
    /// with <c>stopsound(actor, CH_TRIGGER_SINGLE)</c> (CH_TRIGGER_SINGLE == SoundChannel.Item across the port).
    /// The rising-edge pickup notif + glow/alpha/countdown stay in PowerupsMutator.PlayerPreThink; only the
    /// falling edge needs the removal cause, which lives here in m_remove.
    /// </summary>
    private static void PowerupRemove(Entity e, ActiveStatusEffect s, StatusEffectRemoval removal)
    {
        if (Api.Services is null) return;
        // QC strength.qc:5-9: Send_Notification(..., CENTER, ..., CENTER_POWERDOWN_X) only when
        // removal_type == STATUSEFFECT_REMOVE_TIMEOUT && IS_PLAYER(actor). A drop (NORMAL) stays silent.
        if (removal == StatusEffectRemoval.Timeout && (e.Flags & EntFlags.Client) != 0)
        {
            string? center = PowerdownCenter(s.DefId);
            if (center != null)
                NotificationSystem.Center(e, center);
        }
        // QC strength.qc:13: stopsound(actor, CH_TRIGGER_SINGLE) on ANY removal — cuts the powerup pickup fanfare.
        Api.Sound.Stop(e, SoundChannel.Item); // CH_TRIGGER_SINGLE = 3 = SoundChannel.Item
    }

    /// <summary>Map a powerup status-effect def id to its <c>CENTER_POWERDOWN_X</c> notification name (null for
    /// the non-powerup effects, which carry no powerdown print).</summary>
    private static string? PowerdownCenter(int defId)
    {
        var def = ByIndexInRegistry(defId);
        return def?.Name switch
        {
            "strength" => "POWERDOWN_STRENGTH",
            "shield" => "POWERDOWN_SHIELD",
            "speed" => "POWERDOWN_SPEED",
            "invisibility" => "POWERDOWN_INVISIBILITY",
            _ => null,
        };
    }

    /// <summary>QC Burning <c>m_remove</c> (burning.qc:4-8): clear EF_FLAME before the SUPER removal.</summary>
    private static void BurningRemove(Entity e, ActiveStatusEffect s, StatusEffectRemoval removal) => e.Effects &= ~EfFlame;

    /// <summary>QC EF_FLAME (dpextensions.qc:125) — the burning flame the client turns into the orange light +
    /// EF_FLAME particle burst (CsqcModelEffects EF_FLAME). burning.qc:21 ORs it in m_tick, :6 clears in m_remove.</summary>
    private const int EfFlame = 1024;
    /// <summary>QC EF_SHOCK (constants.qh:103 BIT(18)) — the stunned arc/shock aura the client turns into a
    /// blue-white light + particle burst (CsqcModelEffects EF_SHOCK).</summary>
    private const int EfShock = 262144;
    /// <summary>QC EF_ADDITIVE (dpextensions.qc:93) | EF_FULLBRIGHT (dpextensions.qc:133) — the spawnshield
    /// post-game-start shimmer (the same additive+fullbright glow strength/shield use).</summary>
    private const int EfSpawnShieldShimmer = 32 | 512;

    /// <summary>QC Stunned <c>m_tick</c> (stunned.qc:16-26): self-remove while frozen; otherwise OR the
    /// EF_SHOCK aura into the actor's networked .effects (Shock_ApplyDamage is #if'd-out in Base too). The
    /// SUPER tick (PERSISTENT recompute + timeout) runs in <see cref="Tick"/>.</summary>
    private static bool StunnedTick(Entity e, ActiveStatusEffect s)
    {
        if (IsStatusFrozen(e)) return true;   // m_remove(NORMAL) — OnRemove clears EF_SHOCK
        e.Effects |= EfShock;                 // QC actor.effects |= EF_SHOCK
        return false;
    }

    /// <summary>QC Stunned <c>m_remove</c> (stunned.qc:5): clear EF_SHOCK before the SUPER removal.</summary>
    private static void StunnedRemove(Entity e, ActiveStatusEffect s, StatusEffectRemoval removal) => e.Effects &= ~EfShock;

    /// <summary>QC SpawnShield <c>m_tick</c> (spawnshield.qc:11-13): OR EF_ADDITIVE|EF_FULLBRIGHT into the
    /// actor's networked .effects once the match clock has passed game_starttime (so warmup doesn't shimmer).</summary>
    private static bool SpawnShieldTick(Entity e, ActiveStatusEffect s)
    {
        float now = Api.Services != null ? Api.Clock.Time : 0f;
        float gameStart = StartItem.GameStartTimeProvider?.Invoke() ?? 0f;
        if (now >= gameStart)
            e.Effects |= EfSpawnShieldShimmer;   // QC actor.effects |= (EF_ADDITIVE | EF_FULLBRIGHT)
        return false;
    }

    /// <summary>QC SpawnShield <c>m_remove</c> (spawnshield.qc:5): clear EF_ADDITIVE|EF_FULLBRIGHT before SUPER.</summary>
    private static void SpawnShieldRemove(Entity e, ActiveStatusEffect s, StatusEffectRemoval removal) => e.Effects &= ~EfSpawnShieldShimmer;

    /// <summary>QC Superweapon <c>m_persistent</c> (superweapons.qc:4-7): persistent while the entity carries
    /// IT_UNLIMITED_SUPERWEAPONS — this is what sets the networked PERSISTENT bit (the HUD infinity glyph)
    /// instead of the bespoke 999s-timer fake in SuperweaponTimeout.</summary>
    private static bool SuperweaponPersistent(Entity e)
        => (e.Items & (int)ItemFlag.UnlimitedSuperweapons) != 0;

    /// <summary>Look up a def by its RegistryId (== list index), bounds-safe. Null if out of range.</summary>
    private static StatusEffectDef? ByIndexInRegistry(int id)
        => (id >= 0 && id < Registry<StatusEffectDef>.Count) ? Registry<StatusEffectDef>.ById(id) : null;

    private static bool CvarBool(string name)
        => Api.Services != null && Api.Cvars.GetFloat(name) != 0f;

    /// <summary>QC <c>STAT(FROZEN, e) || StatusEffects_active(STATUSEFFECT_Frozen, e)</c>: the gametype
    /// freeze stat OR the frozen status effect (burning/stunned self-extinguish while frozen).</summary>
    private static bool IsStatusFrozen(Entity e)
        => e.FrozenStat != 0 || (Frozen != null && Has(e, Frozen));

    // ============================================================================================
    //  Fire damage-over-time (QC server/damage.qc Fire_AddDamage / Fire_ApplyDamage)
    // ============================================================================================

    /// <summary>QC <c>StatusEffects_gettime(this, actor)</c> (status_effects.qc:17-28): the effect's end time,
    /// clamped up to <paramref name="now"/> so an effect whose timer has just lapsed still reads as active
    /// for the current frame (the burn-stacking math relies on this). Permanent (ExpireTime &lt;= 0) returns now.</summary>
    public static float GetTime(Entity e, StatusEffectDef def, float now)
    {
        foreach (var s in e.StatusEffects)
            if (s.DefId == def.RegistryId)
                return (s.ExpireTime > 0f && s.ExpireTime >= now) ? s.ExpireTime : now;
        return 0f;
    }

    /// <summary>
    /// QC <c>Fire_AddDamage(e, o, d, t, dt)</c> (server/damage.qc:965-1070): ignite (or stack onto an existing
    /// burn) <paramref name="e"/> for total damage <paramref name="d"/> over time <paramref name="t"/> seconds,
    /// attributed to <paramref name="owner"/> with deathtype <paramref name="deathType"/>. Implements the QC
    /// overlap LEMMA so re-igniting a burning target combines damage/time without exceeding maxdps, instead of
    /// the old plain overwrite. Stores the DPS on <see cref="Entity.FireDamagePerSec"/> and ticks via
    /// <see cref="FireApplyDamage"/>. The single faithful entry point ignition sites (fireball/napalm/wyvern,
    /// lava/inferno) should call so the Strength convention is consistent. Returns the damage actually added.
    /// </summary>
    public static float FireAddDamage(Entity e, Entity? owner, float d, float t, string deathType)
    {
        if (Burning is null) return -1f;
        if (d <= 0f) return -1f;
        if (e.DeadState != DeadFlag.No) return -1f;   // QC: IS_PLAYER(e) && IS_DEAD(e)

        float now = Api.Services != null ? Api.Clock.Time : 0f;
        t = MathF.Max(t, 0.1f);
        float dps = d / t;

        if (Has(e, Burning))
        {
            float fireEndTime = GetTime(e, Burning, now);
            float minTime = fireEndTime - now;
            float maxTime = MathF.Max(minTime, t);
            float minDps = e.FireDamagePerSec;
            float maxDps = MathF.Max(minDps, dps);

            if (maxTime > minTime || maxDps > minDps)
            {
                float minDamage = minDps * minTime;
                float maxDamage = minDamage + d;
                float totalDamage = MathF.Min(maxDamage, maxTime * maxDps);
                // alternate (LEMMA): never below mindps, never beyond maxtime.
                float totalTime = (minDps > 0f) ? MathF.Min(maxTime, totalDamage / minDps) : maxTime;

                e.FireDamagePerSec = (totalTime > 0f) ? totalDamage / totalTime : dps;
                Apply(e, Burning, totalTime, dps, owner);
                if (totalDamage > 1.2f * minDamage)
                {
                    e.FireDeathType = deathType;
                    if (!ReferenceEquals(e.FireOwner, owner))
                    {
                        e.FireOwner = owner;
                        e.FireHitSound = false;
                    }
                }
                return MathF.Max(0f, totalDamage - minDamage);
            }
            return 0f;
        }

        e.FireDamagePerSec = dps;
        Apply(e, Burning, t, dps, owner);
        e.FireDeathType = deathType;
        e.FireOwner = owner;
        e.FireHitSound = false;
        return d;
    }

    private const string CvarFireTransferDamage = "g_balance_firetransfer_damage"; // 0.8
    private const string CvarFireTransferTime   = "g_balance_firetransfer_time";   // 0.9

    /// <summary>
    /// QC <c>Fire_ApplyDamage(e)</c> (server/damage.qc:1072-1105): the per-tick burn hit —
    /// <c>fire_damagepersec * min(frametime, fireend-time)</c> dealt with the stored fire deathtype + owner
    /// (frame-rate independent, unlike the old <c>strength*0.05</c>/frame). After the hit, fire TRANSFER
    /// re-ignites every adjacent non-frozen, non-independent, damageable entity that overlaps the burning entity
    /// at <c>g_balance_firetransfer_damage 0.8</c> of the remaining burn DPS over <c>g_balance_firetransfer_time
    /// 0.9</c> of the remaining burn time (QC Fire_ApplyDamage:1094-1104).
    /// </summary>
    private static void FireApplyDamage(Entity e, ActiveStatusEffect s)
    {
        if (Api.Services is null) return;
        float now = Api.Clock.Time;
        float frameTime = Api.Clock.FrameTime;
        if (frameTime <= 0f) frameTime = 1f / 72f;   // headless fallback (SimulationLoop TicRate)

        // Resolve the burn owner: prefer the stored fire_owner, else the application source.
        Entity? owner = e.FireOwner ?? s.Source;
        // Resolve the per-second burn rate: prefer the stacked fire_damagepersec (set by FireAddDamage);
        // fall back to the legacy convention where Strength carries the DPS directly (ignition sites that
        // call StatusEffectsCatalog.Apply(Burning, ..., strength: dps) without going through FireAddDamage).
        float dps = e.FireDamagePerSec > 0f ? e.FireDamagePerSec : s.Strength;

        float fireEndTime = GetTime(e, Burning!, now);
        float t = MathF.Min(frameTime, fireEndTime - now);
        if (t < 0f) t = 0f;
        float dmg = dps * t;
        if (dmg <= 0f) return;

        // QC: preserve hitsound counters (damage.qc:1084-1091) so repeated fire ticks don't multiply the
        // hitsound beep. The port approximates this: suppress HitsoundDamageDealtTotal accumulation for burn
        // ticks after the first (fire_hitsound tracks the "already-ticked" state).
        // (QC saves hi/ty from fire_owner and restores them after Damage if fire_hitsound; in the port the
        // HitsoundDamageDealtTotal on owner is advanced inside DamageSystem.Apply, so we can't retroactively
        // undo it. The cue is already rare enough that the missing suppress is low-impact.)

        string deathType = !string.IsNullOrEmpty(e.FireDeathType) ? e.FireDeathType : DeathTypes.Fire;
        // QC Damage(e, e, fire_owner, d, fire_deathtype, ...): inflictor is the burning entity itself.
        Combat.Damage(e, e, owner, dmg, deathType, e.Origin, System.Numerics.Vector3.Zero);
        e.FireHitSound = true; // QC: fire_hitsound = true after the first tick

        // --- QC fire TRANSFER (Fire_ApplyDamage:1094-1104) ---
        // Spread fire to adjacent entities that overlap this entity's bounding box, gated on the burning entity
        // not being independent or frozen. In QC this iterates `g_damagedbycontents` (all players/monsters who
        // take contents damage); the port finds them via FindInRadius on the entity's center.
        if (e.IsIndependentPlayer) return;
        if (IsStatusFrozen(e)) return;

        // Read g_balance_firetransfer_damage / _time; fall back to stock defaults if unset (0).
        float transferDmgFactor  = Api.Cvars.GetFloat(CvarFireTransferDamage);
        if (transferDmgFactor  == 0f) transferDmgFactor  = 0.8f;
        float transferTimeFactor = Api.Cvars.GetFloat(CvarFireTransferTime);
        if (transferTimeFactor == 0f) transferTimeFactor = 0.9f;

        float remainingTime = fireEndTime - now;
        if (remainingTime <= 0f) return;

        float tTransfer = transferTimeFactor * remainingTime;
        float dTransfer = transferDmgFactor * dps * tTransfer;
        if (dTransfer <= 0f) return;

        // QC: boxesoverlap(e.absmin, e.absmax, it.absmin, it.absmax) — query the burning entity's box directly
        // (FindInBox applies the precise AABB test itself, touching counts); replaces the old half-diagonal+4
        // bounding-sphere FindInRadius + per-entity BoxesOverlap trim (upstream b6e02fe3 pattern). The buffer is
        // a SNAPSHOT — FireAddDamage below never re-queries.
        System.Collections.Generic.List<Entity> nearby = _fireTransferBuffer ??= new();
        Api.Entities.FindInBox(e.AbsMin, e.AbsMax, nearby);

        foreach (Entity it in nearby)
        {
            if (ReferenceEquals(it, e)) continue;
            if (it.DeadState != DeadFlag.No) continue;
            if (it.TakeDamage == DamageMode.No) continue;
            if (it.IsIndependentPlayer) continue;
            FireAddDamage(it, owner, dTransfer, tTransfer, DeathTypes.Fire);
        }
    }

    [System.ThreadStatic] private static System.Collections.Generic.List<Entity>? _fireTransferBuffer;

    // ============================================================================================
    //  Lifecycle mass-clear (QC StatusEffects_removeall / StatusEffects_clearall)
    // ============================================================================================

    /// <summary>
    /// QC <c>StatusEffects_removeall(actor, removal_type)</c> (status_effects.qc:58-66): remove every active
    /// effect, running each effect's m_remove mechanics (so a NORMAL removal plays the per-effect removal
    /// sound). The host wires this to PlayerDies / MonsterDies / ClientDisconnect / MakePlayerObserver /
    /// reset_map_global / PutClientInServer so stale burning/stunned/superweapon timers don't survive
    /// death/respawn/map reset (see cross-file todos for the dispatch sites).
    /// </summary>
    public static void RemoveAll(Entity e, StatusEffectRemoval removal = StatusEffectRemoval.Normal)
    {
        if (e is null || e.StatusEffects.Count == 0) return;
        // Snapshot the present def ids (Remove mutates the list as it goes).
        var ids = new List<int>(e.StatusEffects.Count);
        foreach (var s in e.StatusEffects) ids.Add(s.DefId);
        foreach (int id in ids)
        {
            var def = ByIndexInRegistry(id);
            if (def != null) Remove(e, def, removal);
        }
    }

    /// <summary>
    /// QC <c>StatusEffects_clearall(store)</c>: forcibly drop every effect with NO mechanics or sound (the
    /// CLEAR path — used on a spectatee-effect store and on map reset after the NORMAL pass got the sounds).
    /// Marks the entity dirty so the cleared state networks.
    /// </summary>
    public static void ClearAll(Entity e)
    {
        if (e is null || e.StatusEffects.Count == 0) return;
        e.StatusEffects.Clear();
        Update(e);
    }

    public static bool Has(Entity e, StatusEffectDef def)
    {
        foreach (var s in e.StatusEffects) if (s.DefId == def.RegistryId) return true;
        return false;
    }

    public static void Apply(Entity e, StatusEffectDef def, float duration, float strength = 1f, Entity? source = null)
    {
        float now = Api.Services != null ? Api.Clock.Time : 0f;
        // QC StatusEffects_apply guard (status_effects.qc:33): a positive-but-non-future timer is a no-op
        // (the effect's window is already over). duration <= 0 is the port's deliberate "permanent" convention
        // (used by SuperweaponTimeout's persistence + FreezeTag/NadeIce until-thawed), so it is NOT rejected.
        if (duration > 0f && now + duration <= now)
            return;
        // QC m_apply: automatically enable the ACTIVE flag when applied (sv_status_effects.qc:34).
        // refresh if already present
        for (int i = 0; i < e.StatusEffects.Count; i++)
        {
            if (e.StatusEffects[i].DefId == def.RegistryId)
            {
                var cur = e.StatusEffects[i];
                cur.ExpireTime = duration > 0 ? now + duration : 0f;
                cur.Strength = strength;
                cur.Source = source;
                cur.Flags |= StatusEffectFlags.Active;
                e.StatusEffects[i] = cur;
                Update(e);   // QC StatusEffects_update(actor)
                return;
            }
        }
        e.StatusEffects.Add(new ActiveStatusEffect
        {
            DefId = def.RegistryId,
            ExpireTime = duration > 0 ? now + duration : 0f,
            Strength = strength,
            Source = source,
            Flags = StatusEffectFlags.Active,
        });
        // QC m_apply !wasactive transition (frozen.qc:46-53): the per-effect on-apply mechanics run only the
        // first time the effect becomes active (it was NOT present above). Frozen drops the actor's grapples.
        def.OnApply?.Invoke(e);
        Update(e);   // QC StatusEffects_update(actor)
    }

    /// <summary>QC <c>StatusEffects_remove(this, actor, NORMAL)</c> — the default removal path (plays the
    /// removal sound unless persistent). Equivalent to <c>Remove(e, def, StatusEffectRemoval.Normal)</c>.</summary>
    public static void Remove(Entity e, StatusEffectDef def) => Remove(e, def, StatusEffectRemoval.Normal);

    /// <summary>
    /// QC <c>m_remove(this, actor, removal_type)</c> (sv_status_effects.qc:40-51 + the per-effect overrides):
    /// remove the effect, running the per-effect mechanics and — for a NORMAL removal of an active,
    /// non-persistent effect — the removal sound (<see cref="StatusEffectDef.RemovalSound"/>). TIMEOUT/CLEAR
    /// removals play no sound (TIMEOUT is the expiry path; CLEAR is a forced removal with no mechanics).
    /// </summary>
    public static void Remove(Entity e, StatusEffectDef def, StatusEffectRemoval removal)
    {
        for (int i = 0; i < e.StatusEffects.Count; i++)
        {
            if (e.StatusEffects[i].DefId != def.RegistryId) continue;
            var s = e.StatusEffects[i];
            // QC per-effect m_remove override (runs before SUPER on EVERY removal_type): clear the .effects
            // bit the per-effect m_tick set — spawnshield EF_ADDITIVE|EF_FULLBRIGHT, stunned EF_SHOCK.
            def.OnRemove?.Invoke(e, s, removal);
            // QC m_remove: sound(actor, CH_TRIGGER, m_sound_rm) on a NORMAL removal of an active,
            // non-persistent effect. (sv_status_effects.qc:46; persistent effects stay silent per #2620.)
            if (removal == StatusEffectRemoval.Normal
                && (s.Flags & StatusEffectFlags.Active) != 0
                && (s.Flags & StatusEffectFlags.Persistent) == 0
                && !string.IsNullOrEmpty(def.RemovalSound)
                && Api.Services != null)
            {
                SoundSystem.PlayOn(e, Sounds.ByName(def.RemovalSound!), SoundChannel.Item, SoundLevels.VolBase, SoundLevels.AttenNorm);
            }
            e.StatusEffects.RemoveAt(i);
            Update(e);   // QC m_remove -> StatusEffects_update(actor)
            return;
        }
    }

    /// <summary>
    /// QC <c>StatusEffects_tick(actor)</c> (status_effects.qc:9-15 — FOREACH active effect -> m_tick), fused
    /// with the base <c>m_tick</c> (sv_status_effects.qc:11-27). For each active effect: recompute the
    /// PERSISTENT flag from its <see cref="StatusEffectDef.PersistentCheck"/> (BITSET, dirty-mark on change);
    /// a PERSISTENT effect never times out; otherwise run the per-effect <see cref="StatusEffectDef.OnTick"/>
    /// (which may request a NORMAL self-removal — e.g. burning self-extinguishes in water/while frozen and
    /// applies its fire damage) and finally expire the timer (TIMEOUT removal). Called per damageable entity
    /// by the server loop.
    /// </summary>
    public static void Tick(Entity e, float now)
    {
        // Iterate by id (the active list can be mutated by m_tick/removal). Snapshot the present def ids.
        for (int i = e.StatusEffects.Count - 1; i >= 0; i--)
        {
            if (i >= e.StatusEffects.Count) continue;   // list shrank under us
            var s = e.StatusEffects[i];
            var def = ByIndexInRegistry(s.DefId);
            if (def is null) continue;

            // QC m_tick: BITSET(flg, PERSISTENT, m_persistent(this, actor)); dirty-mark on a real change.
            bool wantPersistent = def.PersistentCheck != null && def.PersistentCheck(e);
            bool isPersistent = (s.Flags & StatusEffectFlags.Persistent) != 0;
            if (wantPersistent != isPersistent)
            {
                if (wantPersistent) s.Flags |= StatusEffectFlags.Persistent;
                else s.Flags &= ~StatusEffectFlags.Persistent;
                e.StatusEffects[i] = s;
                isPersistent = wantPersistent;
                Update(e);   // QC: if(oldflag != flg) StatusEffects_update(actor)
            }

            // QC m_tick: a PERSISTENT effect does NOT time out (sv_status_effects.qc:20-22).
            if (isPersistent)
            {
                def.OnTick?.Invoke(e, s);   // persistent burning still applies fire damage / EF tick
                continue;
            }

            // Per-effect m_tick body (burning fire damage + water/frozen extinguish, stunned frozen-extinguish).
            // A true return is the per-effect m_remove(NORMAL) request.
            if (def.OnTick != null && def.OnTick(e, s))
            {
                Remove(e, def, StatusEffectRemoval.Normal);
                continue;
            }

            // QC m_tick timeout: time > statuseffect_time -> m_remove(TIMEOUT) (no removal sound).
            if (s.ExpireTime > 0f && now >= s.ExpireTime)
                Remove(e, def, StatusEffectRemoval.Timeout);
        }
    }

    // ============================================================================================
    //  PERSISTENT flag (QC m_persistent / STATUSEFFECT_FLAG_PERSISTENT)
    // ============================================================================================

    /// <summary>
    /// Set or clear the PERSISTENT flag on an active effect (QC <c>m_tick</c>: <c>BITSET(flg,
    /// STATUSEFFECT_FLAG_PERSISTENT, m_persistent())</c>, sv_status_effects.qc:16). When the flag changes the
    /// entity is marked dirty so the client overlay (burning/frozen) re-syncs. No-op if the effect is absent.
    /// Returns true if the flag changed.
    /// </summary>
    public static bool SetPersistent(Entity e, StatusEffectDef def, bool persistent)
    {
        for (int i = 0; i < e.StatusEffects.Count; i++)
        {
            if (e.StatusEffects[i].DefId != def.RegistryId) continue;
            var s = e.StatusEffects[i];
            StatusEffectFlags before = s.Flags;
            if (persistent) s.Flags |= StatusEffectFlags.Persistent;
            else s.Flags &= ~StatusEffectFlags.Persistent;
            if (s.Flags == before) return false;
            e.StatusEffects[i] = s;
            Update(e);   // QC: if(oldflag != flg) StatusEffects_update(actor)
            return true;
        }
        return false;
    }

    /// <summary>QC <c>statuseffect_flags[id]</c>: the current flag bitmap for an effect, or 0 if not active.</summary>
    public static StatusEffectFlags FlagsOf(Entity e, StatusEffectDef def)
    {
        foreach (var s in e.StatusEffects)
            if (s.DefId == def.RegistryId) return s.Flags;
        return 0;
    }

    // ============================================================================================
    //  Networking — ENT_CLIENT_STATUSEFFECTS (QC status_effects.qh StatusEffects_Write/Send + the
    //  NET_HANDLE read). Server marks an entity dirty on any apply/remove/flag change (QC SendFlags =
    //  0xFFFFFF in StatusEffects_update); the net tick writes a full bitmap snapshot for each dirty
    //  entity and the client reads it back. Delta-encoding against the previous snapshot is OPTIONAL
    //  per recon — this port writes the full per-entity bitmap each changed tick.
    // ============================================================================================

    /// <summary>QC <c>StatusEffects_groups_minor</c> (status_effects.qh:19): bits per minor group (one byte).</summary>
    public const int GroupsMinor = 8;

    /// <summary>QC <c>StatusEffects_groups_major</c> (status_effects.qh:20): number of major (minor-bitmap) groups.</summary>
    public const int GroupsMajor = 4;

    // Server-side dirty set (QC the per-entity SendFlags): entities whose status-effect state changed since
    // the last net flush. Kept here (not on Entity) because Entity's gameplay-state partial is owned by
    // another file this wave; a side-table is the layer-clean equivalent of SendFlags. The full per-entity
    // bitmap is re-sent each flush (delta-encoding against a stored snapshot is optional per recon).
    private static readonly HashSet<Entity> _dirty = new();

    /// <summary>
    /// QC <c>StatusEffects_update(actor)</c> (status_effects.qh:153, <c>SendFlags = 0xFFFFFF</c>): mark this
    /// entity's status-effect state dirty so the next net flush re-sends its full bitmap. Called from every
    /// apply/remove/flag-change site (and by <c>DamageSystem.ApplyFrozen/ApplyBurning</c>).
    /// </summary>
    public static void Update(Entity e)
    {
        if (e is not null)
            _dirty.Add(e);
    }

    /// <summary>True if <paramref name="e"/> has a pending status-effect update to network (QC SendFlags != 0).</summary>
    public static bool IsDirty(Entity e) => _dirty.Contains(e);

    /// <summary>Clear all pending dirty marks (test/teardown support, map reset).</summary>
    public static void ResetNetworkState() => _dirty.Clear();

    /// <summary>
    /// QC <c>StatusEffects_Write</c> (status_effects.qh:74): encode an entity's status-effect state into the
    /// grouped major/minor-bitmap + per-effect (time, flags) wire format. Layout (mirrors the QC Writebits
    /// order exactly): a <see cref="GroupsMajor"/>-bit majorBits mask; then for each set major group, a
    /// <see cref="GroupsMinor"/>-bit minorBits mask; then for each set minor bit, the effect's
    /// float <c>time</c> (4 bytes) and byte <c>flags</c>. This port writes EVERY active effect (a full
    /// snapshot) rather than only the deltas against a store entity (delta-encoding optional per recon).
    /// </summary>
    public static byte[] Write(Entity e)
    {
        // Build per-id (time, flags) from the active list (id is the effect's RegistryId).
        // majorBits / minorBits[major] from which ids are present.
        int majorBits = 0;
        var minorBits = new int[GroupsMajor];
        var ids = new SortedDictionary<int, (float Time, byte Flags)>();
        foreach (var s in e.StatusEffects)
        {
            int id = s.DefId;
            int maj = id / GroupsMinor;
            int min = id % GroupsMinor;
            if (maj < 0 || maj >= GroupsMajor) continue; // out of the networked range (parity safety net)
            majorBits |= 1 << maj;
            minorBits[maj] |= 1 << min;
            ids[id] = (s.ExpireTime, (byte)s.Flags);
        }

        var buf = new List<byte>(2 + GroupsMajor * (1 + GroupsMinor * 5));
        buf.Add((byte)majorBits);
        for (int i = 0; i < GroupsMajor; i++)
        {
            if ((majorBits & (1 << i)) == 0) continue;
            buf.Add((byte)minorBits[i]);
            for (int j = 0; j < GroupsMinor; j++)
            {
                if ((minorBits[i] & (1 << j)) == 0) continue;
                int id = GroupsMinor * i + j;
                var (t, f) = ids.TryGetValue(id, out var v) ? v : (0f, (byte)0);
                buf.AddRange(System.BitConverter.GetBytes(t)); // WriteFloat
                buf.Add(f);                                    // WriteByte flags
            }
        }
        return buf.ToArray();
    }

    /// <summary>
    /// QC the <c>NET_HANDLE(ENT_CLIENT_STATUSEFFECTS)</c> read (status_effects.qh:48): decode a buffer
    /// produced by <see cref="Write"/> back into (effectId -&gt; time, flags) entries. The reverse of the
    /// major/minor-bitmap walk; used by the client to drive the overlays (and by the round-trip test).
    /// </summary>
    public static Dictionary<int, (float Time, StatusEffectFlags Flags)> Read(byte[] data)
    {
        var result = new Dictionary<int, (float, StatusEffectFlags)>();
        if (data is null || data.Length == 0) return result;
        int p = 0;
        int majorBits = data[p++];
        for (int i = 0; i < GroupsMajor; i++)
        {
            if ((majorBits & (1 << i)) == 0) continue;
            if (p >= data.Length) break;
            int minorBits = data[p++];
            for (int j = 0; j < GroupsMinor; j++)
            {
                if ((minorBits & (1 << j)) == 0) continue;
                if (p + 5 > data.Length) break;
                float t = System.BitConverter.ToSingle(data, p); p += 4;
                byte f = data[p++];
                result[GroupsMinor * i + j] = (t, (StatusEffectFlags)f);
            }
        }
        return result;
    }

    /// <summary>
    /// Flush one dirty entity: if it has a pending update, return its freshly-encoded snapshot and clear the
    /// dirty mark (QC the per-frame Net_LinkEntity send). Returns null if the entity isn't dirty. The host
    /// net loop calls this for each entity with a <see cref="IsDirty"/> mark and ships the bytes under
    /// ENT_CLIENT_STATUSEFFECTS.
    /// </summary>
    public static byte[]? Flush(Entity e)
    {
        if (!_dirty.Remove(e)) return null;
        return Write(e);
    }
}
