// Port of the per-entity ".float foo" fields the ported mutators add in QuakeC
// (e.g. sv_instagib.qc .instagib_nextthink, bloodloss.qc .bloodloss_timer,
// sv_touchexplode.qc .touchexplode_time, multijump.qc .multijump_count/.multijump_ready,
// walljump STAT(LASTWJ), sv_midair.qc .midair_shieldtime).
//
// QuakeC kept these in the flat edict field namespace; the C# entity-model (ADR-0007,
// specs/entity-model.md) promotes them to typed members. Entity is declared partial and this is a
// NEW file, so adding them here respects the task constraint (no existing file modified). All names
// are prefixed with their owning mutator to keep the shared Entity namespace collision-free.

namespace XonoticGodot.Common.Framework
{
    public partial class Entity
    {
        // --- player input intent (QC PHYS_CS(this).movement + pressedkeys + buttons) ---
        // The headless sim doesn't carry a usercmd; these are the fields the movement-driven mutators
        // (dodging double-tap, multijump dodging-redirect, walljump/flight crouch) read each frame.
        // The input layer writes them before PlayerPhysics/PlayerJump runs; defaults keep them inert.
        /// <summary>QC PHYS_CS(this).movement.x — forward (+) / back (-) wish, in the move-speed scale.</summary>
        public float MovementForward;
        /// <summary>QC PHYS_CS(this).movement.y — right (+) / left (-) strafe wish.</summary>
        public float MovementRight;
        /// <summary>QC pressedkeys bitset (KEY_FORWARD/BACKWARD/LEFT/RIGHT/JUMP/CROUCH/ATCK…) from last frame.</summary>
        public int PressedKeys;
        /// <summary>QC PHYS_INPUT_BUTTON_CROUCH — crouch button currently held.</summary>
        public bool ButtonCrouch;
        /// <summary>QC PHYS_INPUT_BUTTON_ATCK2 — secondary-fire button currently held. Published each frame
        /// (alongside <see cref="OffhandFirePressed"/>) for the PlayerPreThink hooks that read it — e.g. the
        /// Overkill countdown-blaster (sv_overkill.qc PlayerPreThink), which fires the secondary blaster during
        /// a round countdown when the normal weapon-fire path has the fire buttons forbidden.</summary>
        public bool ButtonAttack2;
        /// <summary>QC PHYS_INPUT_BUTTON_CHAT — the player has the chat console open (typing). Mirrors the input
        /// <c>Typing</c> intent; read by the campcheck mutator's typecheck gate (sv_campcheck.qc:43).</summary>
        public bool ButtonChat;
        /// <summary>QC v_angle — the player's exact view angles (used for air-dodge / multijump redirect). Falls back to <see cref="Angles"/> when unset.</summary>
        public System.Numerics.Vector3 ViewAngles;

        // --- instagib (sv_instagib.qc) ---
        /// <summary>QC .instagib_nextthink — next time the no-ammo countdown ticks.</summary>
        public float InstagibNextThink;
        /// <summary>QC .instagib_needammo — countdown/"find ammo" warning is active.</summary>
        public bool InstagibNeedAmmo;
        /// <summary>
        /// QC global <c>yoda = 1</c> set by the instagib Damage_Calculate branch (sv_instagib.qc:245-247): the
        /// target had alpha in (0,1) (partially transparent — cloaked/invisible) at the moment of the Vaporizer
        /// hit. The Vaporizer's Announce() reads this instead of IsFlying when the flag is set, matching QC's
        /// <c>if (yoda &amp;&amp; flying)</c> gate (vaporizer.qc:159). Cleared per-attack in Vaporizer.Announce.
        /// </summary>
        public bool InstagibAlphaYoda;

        // --- bloodloss (bloodloss.qc) ---
        /// <summary>QC .bloodloss_timer — next time a bloodloss health-rot tick is allowed.</summary>
        public float BloodlossTimer;

        // --- touchexplode (sv_touchexplode.qc) ---
        /// <summary>QC .touchexplode_time — debounce so a pair doesn't explode every frame.</summary>
        public float TouchExplodeTime;

        // --- midair (sv_midair.qc) ---
        /// <summary>QC .midair_shieldtime — until this time the player is shielded after touching ground.</summary>
        public float MidairShieldTime;

        // --- multijump (multijump.qc) ---
        /// <summary>QC .multijump_count — extra jumps used since leaving the ground.</summary>
        public int MultijumpCount;
        /// <summary>QC .multijump_ready — jump button was released and re-pressed in midair.</summary>
        public bool MultijumpReady;

        // --- walljump (walljump.qc) ---
        /// <summary>QC STAT(LASTWJ) — time of the last wall jump (delay gate).</summary>
        public float LastWallJumpTime;

        // --- offhand_blaster (sv_offhand_blaster.qc) ---
        /// <summary>QC .offhand — NetName of the offhand weapon granted to the player (e.g. "blaster"), or null.</summary>
        public string? OffhandWeapon;
        /// <summary>QC offhand fire button (the +button the offhand blaster is bound to) is held this frame.</summary>
        public bool OffhandFirePressed;
        /// <summary>QC .offhand_nextthink — refire gate for the offhand weapon.</summary>
        public float OffhandNextThink;

        // --- dodging (sv_dodging.qc) ---
        /// <summary>QC .dodging_action — a dodge is in progress (ramping velocity each frame).</summary>
        public float DodgingAction;
        /// <summary>QC .dodging_single_action — the one-shot up-impulse part of the dodge is pending.</summary>
        public float DodgingSingleAction;
        /// <summary>QC .dodging_direction (xy) — movement direction captured at dodge start.</summary>
        public System.Numerics.Vector3 DodgingDirection;
        /// <summary>QC .last_dodging_time — when the current dodge started (delay gate + ramp base).</summary>
        public float LastDodgingTime;
        /// <summary>QC .dodging_force_total — total speed to add across the ramp.</summary>
        public float DodgingForceTotal;
        /// <summary>QC .dodging_force_remaining — portion of the total not yet added.</summary>
        public float DodgingForceRemaining;
        /// <summary>QC .last_FORWARD_KEY_time — last time the forward strafe key was pressed (double-tap gate).</summary>
        public float DodgeLastForwardTime;
        /// <summary>QC .last_BACKWARD_KEY_time.</summary>
        public float DodgeLastBackwardTime;
        /// <summary>QC .last_LEFT_KEY_time.</summary>
        public float DodgeLastLeftTime;
        /// <summary>QC .last_RIGHT_KEY_time.</summary>
        public float DodgeLastRightTime;

        // --- overkill (sv_overkill.qc) ---
        /// <summary>QC .ok_lastwep[slot] — NetName of the weapon held per slot at death, restored on respawn (HMG→MG, RPC→Nex).</summary>
        public readonly string?[] OkLastWeapon = new string?[XonoticGodot.Common.Gameplay.MutatorConstants.MaxWeaponSlots];

        // --- overkill weapons (okmachinegun/okshotgun/oknex/okhmg/okrpc) ---
        /// <summary>
        /// QC <c>actor.jump_interval</c> — the player-level refire gate the Overkill weapons' secondary
        /// blaster-jump uses (every ok* weapon's wr_think gates the blaster on <c>time &gt;= actor.jump_interval</c>
        /// with <c>refire_type == 1</c>). NOTE: distinct from the per-weapon-slot <c>WeaponSlotState.JumpInterval</c>
        /// (the Vaporizer secondary laser) — QC stores this one on the player edict, not the weapon entity.
        /// </summary>
        public float JumpInterval;

        // --- bugrigs (bugrigs.qc) ---
        /// <summary>QC .bugrigs_prevangles — the player's angles stashed each PlayerPhysics so PM_Physics can restore
        /// them (the engine clobbers them between frames; bugrigs drives angles itself).</summary>
        public System.Numerics.Vector3 BugrigsPrevAngles;

        // --- campcheck (sv_campcheck.qc) ---
        /// <summary>QC .campcheck_nextcheck — next time the camp-distance check runs for this player.</summary>
        public float CampcheckNextCheck;
        /// <summary>QC .campcheck_traveled_distance — accumulated 2D distance since the last check.</summary>
        public float CampcheckTraveledDistance;
        /// <summary>QC .campcheck_prevorigin — the player's origin last frame (for the per-frame 2D delta).</summary>
        public System.Numerics.Vector3 CampcheckPrevOrigin;

        // --- damagetext (sv_damagetext.qc) ---
        /// <summary>
        /// QC <c>.int dent_attackers[DENT_ATTACKERS_SIZE]</c> — per-victim bitset of attacker ids that have already
        /// hit this player since its last respawn (drives DTFLAG_STOP_ACCUMULATION: the first hit from a given
        /// attacker after respawn forces the client to start a fresh accumulation group). Cleared on PlayerSpawn.
        /// Modeled as a HashSet of attacker entities (the port has no stable dense client-id), which is the faithful
        /// "have I been hit by this attacker yet this life" set.
        /// </summary>
        public readonly System.Collections.Generic.HashSet<Entity> DentAttackers = new();

        // --- itemstime (itemstime.qc) ---
        // NOTE: QC's .scheduledrespawntime — the absolute time a world item is scheduled to (re)spawn at — already
        // lives on the entity (Gameplay/Items/EntityItemState.cs, set by ItemPickupRules), so the itemstime
        // producer reads it directly; no new field is needed here.

        // --- nix (sv_nix.qc, per-player) ---
        /// <summary>QC .nix_lastchange_id — the round-change id this player last synced its weapon/ammo to.</summary>
        public float NixLastChangeId = -1f;
        /// <summary>QC .nix_nextincr — next time the ammo trickle gives a little more ammo.</summary>
        public float NixNextIncr;
        /// <summary>QC .nix_lastinfotime — last countdown value the player was notified of (de-dupes the message).</summary>
        public float NixLastInfoTime;

        // --- buffs (sv_buffs.qc + buff/*) ---
        /// <summary>QC .buff_shield — until this time the player can't pick up / be given a buff (drop cooldown).</summary>
        public float BuffShield;
        /// <summary>QC .buff_effect_delay — throttle for the buff particle effect.</summary>
        public float BuffEffectDelay;
        /// <summary>QC .buff_flight_oldgravity — the player's gravity before the flight buff flipped it.</summary>
        public float BuffFlightOldGravity;
        /// <summary>QC .buff_flight_crouchheld — crouch was held this frame (one flight gravity-flip per press).</summary>
        public bool BuffFlightCrouchHeld;
        /// <summary>QC .buff_ammo_prev_infitems — whether the player already had unlimited ammo before the ammo buff.</summary>
        public bool BuffAmmoPrevInfItems;
        /// <summary>QC .buffdef — the buff type a buff pickup entity currently carries (null = none/random).</summary>
        public XonoticGodot.Common.Gameplay.StatusEffectDef? BuffDef;
        /// <summary>QC .buff_active — a buff pickup is currently collectable (vs. on its respawn cooldown).</summary>
        public bool BuffActive;
        /// <summary>QC .lifetime (on a buff pickup) — absolute time the untouched buff re-randomizes/relocates
        /// (g_buffs_random_lifetime); 0 = no lifetime timer running.</summary>
        public float BuffLifetime;
        /// <summary>QC buff spawnflag 64 ("always randomize/relocate") — set on auto-seeded buffs by
        /// buffs_DelayedInit so they relocate on every reset even when g_buffs_random_location is 0.</summary>
        public bool BuffAlwaysRelocate;
        /// <summary>QC .team_forced (on a buff pickup) — a teamplay buff item only pickupable by this team number;
        /// 0 = any team. Set by buff_Init_Compat from spawnflags 2/4.</summary>
        public int TeamForced;

        // --- movement stat overrides (QC STAT(MOVEVARS_*)) set by movement-affecting buffs ---
        // The C# successors to QC's per-player movement stats. The buffs PlayerPhysics hook writes these
        // each frame (and resets them first), so the movement integrator can honour the boosted jump /
        // scaled top speed. A sentinel of 0 / 1 means "no override".
        /// <summary>QC STAT(MOVEVARS_JUMPVELOCITY) — per-player jump velocity override (0 = use the cvar default).</summary>
        public float JumpVelocityOverride;
        /// <summary>QC STAT(MOVEVARS_HIGHSPEED) — per-player top-speed multiplier (1 = unscaled).</summary>
        public float SpeedMultiplier = 1f;
        /// <summary>The server's resolved <see cref="SpeedMultiplier"/> replicated to the local CLIENT-PREDICTION
        /// carrier (owner snapshot). The carrier has none of the powerup/buff/nade status effects that the
        /// PlayerPhysics speed hook reads, so it can't recompute the multiplier itself — it adopts this each
        /// predicted tick so a Speed powerup / speed buff / entrap slow reaches the same top speed as authority
        /// (QC replicates STAT(MOVEVARS_HIGHSPEED) per-player). 1 = unscaled; only the predicted leg reads it.</summary>
        public float SpeedMultiplierPredicted = 1f;
    }

    /// <summary>
    /// QuakeC EF_* effect bits (model rendering flags packed into <see cref="Entity.Effects"/>, an int).
    /// Only the handful the ported mutators touch are mirrored here; values match the engine constants.
    /// </summary>
    public static class EffectFlags
    {
        public const int FullBright = 512;   // EF_FULLBRIGHT (dpextensions.qc:133) — was mislabeled as 8 (=EF_DIMLIGHT);
                                             //   networked via Entity.Effects + read by the client csqcmodel hooks (T58),
                                             //   so it MUST be the engine value or instagib/buffs fullbright never renders.
        public const int Additive   = 32;    // EF_ADDITIVE
        public const int NoDraw     = 16;    // EF_NODRAW — model not rendered
        public const int NoShadow   = 4096;  // EF_NOSHADOW (dpextensions.qc:171) — was mislabeled as 8192 (=EF_NODEPTHTEST)
        public const int NoDepthTest = 8192; // EF_NODEPTHTEST (dpextensions.qc) — draw-through-walls (g_nodepthtestplayers)
        public const int Stardust   = 2048;  // EF_STARDUST — sparkle particles (buff pickups)
        public const int RestartAnim = 1 << 20; // EF_RESTARTANIM_BIT (dpextensions.qc:187) — restart the model anim
        public const int Teleport    = 1 << 21; // EF_TELEPORT_BIT (dpextensions.qc:205) — teleport sparkle on (re)spawn
    }

    /// <summary>
    /// QuakeC pressedkeys bits (KEY_* in dpdefs / sv_dodging). The dodging double-tap detector mirrors the
    /// previous frame's pressed directions here so a new press is distinguishable from a held key.
    /// </summary>
    [Flags]
    public enum PressedKeyBits
    {
        None     = 0,
        Forward  = 1 << 0,  // KEY_FORWARD
        Backward = 1 << 1,  // KEY_BACKWARD
        Left     = 1 << 2,  // KEY_LEFT
        Right    = 1 << 3,  // KEY_RIGHT
        Jump     = 1 << 4,  // KEY_JUMP
        Crouch   = 1 << 5,  // KEY_CROUCH
        Attack1  = 1 << 6,  // KEY_ATCK
        Attack2  = 1 << 7,  // KEY_ATCK2
    }
}

namespace XonoticGodot.Common.Gameplay
{
    /// <summary>
    /// Shared constants for the ported mutators that QC kept as global #defines. Lives in the Mutators
    /// folder (a new file) so it doesn't touch the shared Inventory/weapon code.
    /// </summary>
    public static class MutatorConstants
    {
        /// <summary>QC MAX_WEAPONSLOTS — number of simultaneous weapon-entity slots a player carries.</summary>
        public const int MaxWeaponSlots = 2;

        /// <summary>QC Q3SURFACEFLAG_SKY (BIT(2)) — surface tagged as the sky (movement traces ignore it).</summary>
        public const int Q3SurfaceFlagSky = 0x4;

        /// <summary>QC Q3SURFACEFLAG_NOIMPACT (BIT(0)) — surface that nothing impacts (skip for wall jumps).</summary>
        public const int Q3SurfaceFlagNoImpact = 0x1;
    }
}
