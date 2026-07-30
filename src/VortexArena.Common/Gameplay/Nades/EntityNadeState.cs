// Port of the per-entity ".float/.entity" nade fields QuakeC adds in
// qcsrc/common/mutators/mutator/nades/sv_nades.qc + nade/*.qc (.nade, .fake_nade, .nade_refire,
// .nade_time_primed, STAT(NADE_BONUS/NADE_BONUS_SCORE/NADE_BONUS_TYPE/NADE_TIMER), .nade_spawnloc,
// .nade_entrap_time, .nade_veil_time/.nade_veil_prevalpha, STAT(NADE_DARKNESS_TIME), .pokenade_type,
// .nade_altbutton, .nade_lifetime, STAT(NADES_SMALL)).
//
// QuakeC kept these in the flat edict field namespace; the C# entity-model (ADR-0007,
// specs/entity-model.md) promotes them to typed members. Entity is declared partial and this is a NEW
// file, so adding them here respects the task constraint (no existing file modified). Some fields live on
// the PLAYER entity (Nade/FakeNade/NadeRefire/bonus/spawnloc/entrap/veil/darkness), some on the thrown
// NADE entity (NadeTimePrimed/NadeLifetime), and NadeBonusType/NadesSmall are read on both — they all
// share the one partial-Entity namespace as in QC.

namespace XonoticGodot.Common.Framework
{
    public partial class Entity
    {
        // --- held / thrown nade refs (sv_nades.qc .nade / .fake_nade) ---
        /// <summary>QC <c>.nade</c> — the live held/primed nade entity this player is charging (null = none).</summary>
        public Entity? Nade;
        /// <summary>QC <c>.fake_nade</c> — the first-person view-model nade entity shown while charging.</summary>
        public Entity? FakeNade;

        // --- refire / charge timers ---
        /// <summary>QC <c>.nade_refire</c> — earliest time the player may prime the next nade.</summary>
        public float NadeRefire;
        /// <summary>QC <c>.nade_time_primed</c> (on the nade entity) — time the nade was primed (charge base).</summary>
        public float NadeTimePrimed;
        /// <summary>QC <c>.nade_lifetime</c> (on the nade entity) — the lifetime it was primed with (charge denominator).</summary>
        public float NadeLifetime;
        /// <summary>QC <c>.wait</c> (on the held/thrown nade entity) — the absolute time the nade auto-detonates.</summary>
        public float NadeWait;

        // --- bonus nades (STAT(NADE_BONUS) / STAT(NADE_BONUS_SCORE)) ---
        /// <summary>QC STAT(NADE_BONUS) — number of banked bonus nades the player can throw without refire.</summary>
        public int NadeBonus;
        /// <summary>QC STAT(NADE_BONUS_SCORE) — accrued fraction (0..1) toward the next bonus nade.</summary>
        public float NadeBonusScore;
        /// <summary>QC STAT(NADE_BONUS_TYPE) — the Nade registry id used for bonus/strength/spawned nades.</summary>
        public int NadeBonusType;

        // --- HUD charge progress (STAT(NADE_TIMER)) ---
        /// <summary>QC STAT(NADE_TIMER) — 0..1 charge progress of the held nade (drives the HUD ring).</summary>
        public float NadeTimer;

        // --- spawn nade (nade/spawn.qc .nade_spawnloc) ---
        /// <summary>QC <c>.nade_spawnloc</c> — the spawn-nade relocation marker; spawns send the player here.</summary>
        public Entity? NadeSpawnLoc;

        // --- entrap / veil / darkness fields (nade/entrap.qc, veil.qc, darkness.qc) ---
        /// <summary>QC <c>.nade_entrap_time</c> — until this time the entity's move speed is slowed by the entrap orb.</summary>
        public float NadeEntrapTime;
        /// <summary>QC <c>.nade_veil_time</c> — until this time the entity is veiled (alpha hidden) by a veil orb.</summary>
        public float NadeVeilTime;
        /// <summary>QC <c>.nade_veil_prevalpha</c> — the entity's alpha before the veil orb hid it (restored on lapse).</summary>
        public float NadeVeilPrevAlpha;
        /// <summary>QC STAT(NADE_DARKNESS_TIME) — until this time the player is blinded by a darkness field (CSQC overlay).</summary>
        public float NadeDarknessTime;

        // --- monster/pokenade type + throw button (sv_nades.qc .pokenade_type / .nade_altbutton) ---
        /// <summary>QC <c>.pokenade_type</c> — the monster netname a monster (poke) nade spawns.</summary>
        public string? PokenadeType;
        /// <summary>QC <c>.nade_altbutton</c> — whether the nade alt button is currently held (offhand charge gate).</summary>
        public bool NadeAltButton;

        // --- small-nade option (STAT(NADES_SMALL)) ---
        /// <summary>QC STAT(NADES_SMALL) — g_nades_nade_small: throw a smaller (harder to shoot) nade hull.</summary>
        public bool NadesSmall;

        // --- nade entity bookkeeping (sv_nades.qc thrown-nade fields reused from the projectile path) ---
        /// <summary>QC <c>.toss_time</c> (on the nade entity) — when the nade was tossed (freezetag revive-nade window).</summary>
        public float NadeTossTime;
        /// <summary>QC <c>.spawnshieldtime</c> reuse on the nade — earliest time the nade can be picked up again.</summary>
        public float NadePickupShieldTime;
        /// <summary>
        /// QC the orb particle gate (<c>.nade_show_particles</c> + <c>.nade_special_time</c>) on orb/fountain
        /// nade entities: whether THIS frame the orb should emit its heal/ammo particle (throttled to ~20 Hz).
        /// </summary>
        public bool NadeShowParticles;
        /// <summary>QC <c>.nade_special_time</c> — next time the orb/fountain fires its periodic effect/spawn.</summary>
        public float NadeSpecialTime;
        /// <summary>QC <c>.ltime</c> reuse on orb nades — engine time the orb expires (lifetime end).</summary>
        public float NadeOrbExpire;
        /// <summary>QC <c>.cnt</c> reuse on the spawn-loc marker — remaining spawns at the spawn-nade location.</summary>
        public int NadeSpawnCount;

        // --- client orb proxy (networked NadeOrbState field on a nade_orb entity) ---
        /// <summary>
        /// Client-only proxy of the networked <c>OrbRadius</c> (qu, 250..500) decoded into a
        /// <c>nade_orb</c> entity from <c>EntityField.NadeOrbState</c>. The renderer uses it for the 3D
        /// model scale and the in-orb color-flash containment test; 0 on every non-orb entity (the
        /// <c>NadeOrbState</c> field is present only on a nade_orb delta).
        /// </summary>
        public float OrbRadiusClient;
    }
}
