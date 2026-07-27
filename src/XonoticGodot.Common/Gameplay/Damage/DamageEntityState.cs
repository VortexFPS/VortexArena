namespace XonoticGodot.Common.Framework;

/// <summary>
/// Per-entity state the DAMAGE pipeline keeps between calls — the C# successor to the flat QuakeC
/// fields that <c>Damage()</c> / <c>PlayerDamage()</c> / <c>PlayerCorpseDamage()</c> (server/damage.qc,
/// server/player.qc) read and write. Kept in a NEW partial <see cref="Entity"/> file (Framework
/// namespace) so the damage port can carry the fields it needs without touching the existing
/// <c>Framework/Entity.cs</c> or the sealed <see cref="Player"/> class (per the porting constraints).
///
/// Field names are deliberately distinct from the existing <see cref="Entity"/> members. These mirror
/// the QC fields used by the damage/death code:
/// <list type="bullet">
///   <item><c>.dmg_take</c>/<c>.dmg_save</c> -> <see cref="DmgTake"/>/<see cref="DmgSave"/> (HUD damage feedback).</item>
///   <item><c>.damageforcescale</c> -> <see cref="DamageForceScale"/> (knockback receptivity; 0 = immovable).</item>
///   <item><c>.dmg_team</c> -> <see cref="DmgTeam"/> (accumulated team-damage for the threshold/mirror rules).</item>
///   <item><c>CS(player).m_handicap_give</c>/<c>m_handicap_take</c> -> <see cref="HandicapGive"/>/<see cref="HandicapTake"/>.</item>
///   <item><c>.pain_finished</c> -> <see cref="PainFinished"/> (pain-anim debounce).</item>
///   <item><c>.pauseregen_finished</c> -> <see cref="PauseRegenFinished"/> (regen pause after a hit).</item>
///   <item><c>.pushltime</c>/<c>.pusher</c> -> <see cref="PushLTime"/>/<see cref="Pusher"/> (credited-attacker window).</item>
///   <item><c>.fire_*</c> -> the <see cref="FireDamagePerSec"/> / <see cref="FireDeathType"/> / <see cref="FireOwner"/> burn fields.</item>
///   <item><c>STAT(AIR_FINISHED)</c> -> <see cref="AirFinished"/> (drowning timer; reset to 0 on death).</item>
///   <item><c>STAT(FROZEN)</c> -> <see cref="FrozenStat"/> (gametype freeze, e.g. Freeze Tag — distinct from the
///         <c>STATUSEFFECT_Frozen</c> status effect tracked in <see cref="Entity.StatusEffects"/>).</item>
///   <item><c>.canteamdamage</c> -> <see cref="CanTeamDamage"/> (entities like the nade that always take team damage).</item>
///   <item><c>.ballistics_density</c> -> <see cref="BallisticsDensity"/> (corpse vs. live density).</item>
/// </list>
/// </summary>
public partial class Entity
{
    // --- damage feedback (QC .dmg_take / .dmg_save) ---
    /// <summary>QC <c>.dmg_take</c>: health damage taken this frame (for the HUD damage indicator).</summary>
    public float DmgTake;
    /// <summary>QC <c>.dmg_save</c>: armor damage absorbed this frame (for the HUD damage indicator).</summary>
    public float DmgSave;

    // --- knockback receptivity (QC .damageforcescale) ---
    /// <summary>
    /// QC <c>.damageforcescale</c>: how strongly this entity reacts to knockback force. 0 means the entity
    /// is never pushed (the QC <c>if (targ.damageforcescale && force)</c> gate). Players set it to
    /// <c>g_player_damageforcescale</c> on spawn; projectiles/static brushes leave it 0.
    /// </summary>
    public float DamageForceScale;

    // --- teamplay damage accounting (QC .dmg_team / CS(player).teamkill_*) ---
    /// <summary>QC <c>.dmg_team</c>: running total of team damage this attacker has dealt (threshold/mirror).</summary>
    public float DmgTeam;
    /// <summary>QC <c>CS(attacker).teamkill_complain</c>: sim time after which the next teamkill complaint voice may fire (5s cooldown).</summary>
    public float TeamKillComplainTime;
    /// <summary>QC <c>CS(attacker).teamkill_soundtime</c>: sim time at which the deferred complaint voice should play (0.4s delay). 0 = none pending.</summary>
    public float TeamKillSoundTime;
    /// <summary>QC <c>CS(attacker).teamkill_soundsource</c>: the victim entity to play the "teamshoot" voice on.</summary>
    public Entity? TeamKillSoundSource;

    // --- handicap (QC CS(player).m_handicap_give / m_handicap_take) ---
    /// <summary>QC forced+voluntary "give" handicap (damage this entity DEALS is divided by this). 1 = none.</summary>
    public float HandicapGive = 1f;
    /// <summary>QC forced+voluntary "take" handicap (damage this entity TAKES is multiplied by this). 1 = none.</summary>
    public float HandicapTake = 1f;

    /// <summary>
    /// QC <c>.handicap_level</c> (server/handicap.qh:64): an int 0..16 mapped from the both-ways average
    /// total handicap (1.0..HANDICAP_MAX_LEVEL_EQUIVALENT=2.0). In Base this is networked (ent_cs) to color the
    /// <c>player_handicap</c> scoreboard icon. Computed here by <c>Handicap_UpdateHandicapLevel</c>; the port has
    /// no ent_cs handicap stat / scoreboard-icon consumer yet, so the value is authoritative-only. 0 = no handicap.
    /// </summary>
    public int HandicapLevel;

    // --- pain / regen timers ---
    /// <summary>QC <c>.pain_finished</c>: sim time until which a new pain animation/sound is suppressed.</summary>
    public float PainFinished;
    /// <summary>QC <c>.pauseregen_finished</c>: sim time until which health/armor regeneration is paused (after a hit).</summary>
    public float PauseRegenFinished;
    /// <summary>QC <c>.pauserothealth_finished</c>: sim time until which health ROT is paused (after a health pickup / spawn).</summary>
    public float PauseRotHealthFinished;
    /// <summary>QC <c>.pauserotarmor_finished</c>: sim time until which armor ROT is paused.</summary>
    public float PauseRotArmorFinished;
    /// <summary>QC <c>.pauserotfuel_finished</c>: sim time until which fuel ROT is paused. (Fuel REGEN shares
    /// <see cref="PauseRegenFinished"/>, like QC — the jetpack/hook write that.)</summary>
    public float PauseRotFuelFinished;

    // --- credited-attacker window (QC .pusher / .pushltime) ---
    /// <summary>QC <c>.pusher</c>: the last player who damaged this entity (credited for kills within the window).</summary>
    public Entity? Pusher;
    /// <summary>QC <c>.pushltime</c>: sim time until which <see cref="Pusher"/> stays the credited attacker.</summary>
    public float PushLTime;
    /// <summary>QC <c>.istypefrag</c>: the victim was typing (chatting) when last damaged.</summary>
    public bool IsTypeFrag;

    // --- fire / burning (QC .fire_*) ---
    /// <summary>QC <c>.fire_damagepersec</c>: current burn DPS while STATUSEFFECT_Burning is active.</summary>
    public float FireDamagePerSec;
    /// <summary>QC <c>.fire_deathtype</c>: the deathtype credited for burn damage.</summary>
    public string FireDeathType = "";
    /// <summary>QC <c>.fire_owner</c>: who set the entity on fire (credited for the burn kill).</summary>
    public Entity? FireOwner;
    /// <summary>QC <c>.fire_hitsound</c>: whether the burn should play a hit sound this tick.</summary>
    public bool FireHitSound;

    // --- hit-confirmation feedback (QC .hitsound_damage_dealt / .typehitsound / .killsound) ---
    // Per-frame accumulators the damage/obituary code banks on the ATTACKER; GameWorld's end-of-frame flush
    // (QC world.qc EndFrame:2507) turns them into the HIT_TIME/TYPEHIT_TIME/KILL_TIME stats below with the
    // typehit > kill > hit priority chain, then clears them. The client (view.qc HitSound) plays one of the
    // three feedback sounds per advance.
    /// <summary>QC <c>.hitsound_damage_dealt</c>: damage dealt to enemies this frame (pre-armor-split amount).</summary>
    public float HitSoundDamageDealt;
    /// <summary>QC <c>.typehitsound</c>: team hits / hits on a chat-protected victim this frame.</summary>
    public int TypeHitSoundCount;
    /// <summary>QC <c>.killsound</c>: enemy frags scored this frame (Obituary's MURDER branch).</summary>
    public int KillSoundCount;

    // --- the flushed hit-feedback stats (QC STAT(HIT_TIME) / STAT(TYPEHIT_TIME) / STAT(KILL_TIME)) ---
    /// <summary>QC <c>STAT(HIT_TIME)</c>: sim time of the last frame this player damaged an enemy.</summary>
    public float HitTime;
    /// <summary>QC <c>STAT(TYPEHIT_TIME)</c>: sim time of the last frame this player team-hit / typehit someone.</summary>
    public float TypeHitTime;
    /// <summary>QC <c>STAT(KILL_TIME)</c>: sim time of the last frame this player fragged an enemy.</summary>
    public float KillTime;

    // --- drowning / freeze stats ---
    /// <summary>QC <c>STAT(AIR_FINISHED)</c>: sim time the player runs out of air (drowning). 0 = not set.</summary>
    public float AirFinished;
    /// <summary>
    /// QC <c>STAT(FROZEN, e)</c>: the gametype freeze flag (Freeze Tag / Clan Arena ice). The damage code
    /// treats a frozen target specially (still takes lethal damage to allow ice-shatter, but suppresses
    /// pain feedback and the same-team hit-sound). Distinct from the STATUSEFFECT_Frozen status effect.
    /// </summary>
    public int FrozenStat;
    /// <summary>
    /// QC <c>.revival_time</c> (sv_freezetag.qh:56): sim time the player was last revived/unfrozen. The ice
    /// nade (ice.qc:59) refuses to re-freeze a player for 1.5s after a revive so a just-thawed player gets a
    /// brief grace window. Set by FreezeTag.Unfreeze; 0 = never revived (eligible).
    /// </summary>
    public float RevivalTime;
    /// <summary>QC <c>.freeze_time</c>: sim time a freeze is held until (used by the weaponstats validity check).</summary>
    public float FreezeTime;
    /// <summary>QC <c>.freezetag_frozen_armor</c>: the player's armor snapshot saved on every hit while frozen, so a
    /// void/lava soft-kill (g_frozen_damage_trigger 0) can restore it after the relocate. 0 outside Freeze Tag.</summary>
    public float FrozenArmor;
    /// <summary>QC <c>.freezetag_frozen_force</c>: accumulated hit force applied to a frozen player this frame, capped
    /// at g_freezetag_revive_auto_reducible_maxforce so multi-projectile weapons can't over-reduce the auto-thaw.</summary>
    public float FrozenForce;

    // --- misc damage flags ---
    /// <summary>
    /// QC <c>.mass</c> (MOVETYPE_PHYSICS rigid bodies): used to scale the force-at-pos impulse the blast
    /// applies. 0 means "use mass 1" in the knockback math. Only meaningful for physics-movetype entities.
    /// </summary>
    public float Mass;

    /// <summary>
    /// QC <c>STATUSEFFECT_SpawnShield</c> expiry: sim time the spawn shield protects this PLAYER until.
    /// (Named distinctly from the item-side <c>SpawnShieldTime</c> in <c>Items/EntityItemState.cs</c>, which
    /// is the world-item "live" flag.) The status-effect catalog port models frozen/burning/buffs but not
    /// spawn-shield, so the damage pipeline tracks it here. <c>&gt; sim-time</c> means the shield is active.
    /// Set on (re)spawn by the spawn system; cleared to 0 on an explicit kill / team change.
    /// </summary>
    public float SpawnShieldExpire;

    /// <summary>QC <c>.canteamdamage</c>: entity always takes team damage in teamplay_mode 4 (e.g. a thrown nade).</summary>
    public bool CanTeamDamage;
    /// <summary>QC <c>.ballistics_density</c>: bullet-penetration density (corpse uses the corpse density).</summary>
    public float BallisticsDensity;
    /// <summary>
    /// QC corpse marker: a dead body re-typed to <c>classname == "body"</c>. Once true the entity uses the
    /// corpse damage path (<c>PlayerCorpseDamage</c>) rather than the live <c>PlayerDamage</c> path.
    /// </summary>
    public bool IsCorpse;
    /// <summary>QC <c>.alpha</c>: render alpha. The gib path sets it to -1 to mark the corpse fully gibbed.</summary>
    public float Alpha = 1f;

    // --- convenience accessors mirroring the QC IS_INDEPENDENT_PLAYER / realowner concepts ---

    /// <summary>
    /// QC <c>IS_INDEPENDENT_PLAYER(e)</c>: a player in an independent (LMS-style) mode whose damage is
    /// isolated from others. No such mode is wired in this port yet, so this is always false (the QC
    /// nullify-for-independent branch then never triggers). Kept so the damage code reads 1:1.
    /// </summary>
    public bool IsIndependentPlayer => false;

    /// <summary>QC <c>.realowner</c>: the player a projectile/turret belongs to (kept as <see cref="Entity.Owner"/>).</summary>
    public Entity? RealOwner => Owner;
}
