using System.Numerics;

namespace XonoticGodot.Common.Framework;

public delegate void EntityThink(Entity self);
public delegate void EntityTouch(Entity self, Entity other);
public delegate void EntityUse(Entity self, Entity activator);

/// <summary>
/// QC <c>.contentstransition(float nOriginalContents, float nNewContents)</c> (dpextensions.qc:1557): a
/// per-entity callback the movetype layer fires when the entity crosses a content boundary (water/slime/
/// lava/empty), passing the previous and new native CONTENTS_* values. Base's movetype code itself emits NO
/// sound — the splash/exit cue and any lava/slime contact-damage hookup live entirely in this callback, set
/// per-entity by gameplay code. Unset (null) = no transition effect.
/// </summary>
public delegate void EntityContentsTransition(Entity self, int prevContents, int newContents);

/// <summary>
/// Marker for the presentation-side binding (a Godot node wrapper, set on the client only).
/// Defined as an interface so <c>XonoticGodot.Common</c> stays Godot-free (ADR-0008).
/// </summary>
public interface IEntityPresence { }

/// <summary>
/// The C# successor to the QuakeC edict. Engine fields live here; gameplay state lives on subclasses
/// or components (resolving QC's flat field namespace — see planning/specs/entity-model.md and ADR-0007).
/// The simulation operates on <see cref="Entity"/> with no Godot dependency.
///
/// Declared <c>partial</c> so feature areas can extend it in their own files without contention.
/// </summary>
public partial class Entity
{
    // --- identity / lifecycle ---
    public int Index;                  // engine-assigned slot
    public bool IsFreed;
    public string ClassName = "";
    public IEntityPresence? Presence;  // link to the Godot node (client-side only)

    // --- spatial state (engine-maintained) ---
    // (QC <c>.oldvelocity</c> = Entity.OldVelocity lives on the vehicles partial, Vehicles/VehicleCommon.cs;
    // it is the shared engine last-frame velocity snapshot, distinct from OldOrigin the interpolation anchor.)
    public Vector3 Origin, OldOrigin, Velocity, Angles, AVelocity;
    public Vector3 Mins, Maxs, Size, AbsMin, AbsMax, ViewOfs, PunchAngle, PunchVector;

    // --- movement / physics ---
    public MoveType MoveType;
    public Solid Solid;

    /// <summary>
    /// DP <c>.dphitcontentsmask</c> (Base/darkplaces/sv_phys.c SV_GenericHitSuperContentsMask): when nonzero,
    /// this SUPERCONTENTS mask OVERRIDES the solid-derived default for THIS entity's own movement trace. A
    /// projectile set up by <c>PROJECTILE_MAKETRIGGER</c> (SOLID_CORPSE) sets it to
    /// <c>SOLID|BODY|CORPSE</c> so the projectile still clips corpses even though SOLID_CORPSE alone would
    /// drop the CORPSE bit. 0 = unset (fall back to the solid-derived mask). See <c>Projectiles.MakeTrigger</c>.
    /// </summary>
    public int DpHitContentsMask;

    /// <summary>
    /// [sv-antilag.clear.on_spawn] Sticky one-shot request to wipe this entity's lag-comp position ring
    /// (port of the explicit <c>antilag_clear</c> call Base fires from <c>PutClientInServer</c>
    /// (client.qc:858) and on vehicle enter/exit). Set by (re)spawn / teleport / vehicle-boarding; the net
    /// driver clears the ring and resets the flag on its next antilag record pass. This catches the case the
    /// per-tick origin-jump heuristic misses — a respawn/teleport that lands WITHIN the jump threshold of the
    /// previous origin would otherwise leave stale history, letting a shot rewind toward the old position.
    /// </summary>
    public bool AntilagNeedsClear;

    public EntFlags Flags;
    public Entity? GroundEntity;
    public int WaterLevel;
    public int WaterType;
    public float Gravity = 1f;

    // --- scheduling / callbacks ---
    public float NextThink;
    public float LTime;               // local time for moving brush entities (PUSH)
    public EntityThink? Think;
    public EntityTouch? Touch;
    public EntityUse? Use;
    public EntityTouch? Blocked;
    public EntityContentsTransition? ContentsTransition;  // QC .contentstransition (movetype water/content-crossing hook)

    // --- commonly shared gameplay fields (kept on base where QC used them generically) ---
    public float Health, MaxHealth;
    public float Frame, Skin;
    public int Effects, ModelIndex;
    public DamageMode TakeDamage;
    public DeadFlag DeadState;
    public Entity? Owner, Enemy, GoalEntity, Aiment, Chain, DmgInflictor;
    public string Model = "", NetName = "", Target = "", Target2 = "", TargetName = "", Message = "";
    public int SpawnFlags, Items;
    public float Team, Frags;

    /// <summary>[r15 #43] QC entcs <c>.clientcolors</c> as networked to the CLIENT proxy: the packed
    /// <c>16*shirt+pants</c> palette colors (FFA profile color or the team-forced <c>17*teamcode</c>).
    /// 0 = colorless (render falls back to the team path). Server-side gameplay reads
    /// <c>Player.ClientColors</c> instead; this field exists for the client render entity.</summary>
    public int Colors;

    // --- bot-avoidance / dodging (QC .bot_dodge / .bot_dodgerating) ---
    /// <summary>
    /// QC <c>.bot_dodge</c> — marks this entity as a projectile/hazard the havocbot dodge logic should avoid.
    /// Projectiles set this to true so bots calculate evasive maneuvers. Default false.
    /// See <c>havocbot_dodge(entity this)</c> (server/bot/default/havocbot/havocbot.qc:1773), which iterates
    /// <c>findchainfloat(bot_dodge, true)</c> over the g_bot_dodge list. The danger-list consumer IS ported
    /// (BotBrain.HavocbotDodge, SUPERBOT-gated): a SUPERBOT bot swerves away from a flagged incoming hazard.
    /// </summary>
    public bool BotDodge;

    /// <summary>
    /// QC <c>.bot_dodgerating</c> — relative danger/damage of a dodgeable projectile, used by the havocbot
    /// dodge calculation to weight the evasion vector. Typically the damage the projectile deals (e.g., a
    /// Blaster bolt sets this to its damage 20). The bot_dodge flag must be true for this to have effect.
    /// Default 0.
    /// </summary>
    public float BotDodgeRating;

    /// <summary>
    /// QC <c>.prevric</c> (shotgun.qc:398) — the last time this actor played a bullet-impact ricochet ping.
    /// The impact FX throttles the ricochet sound to at most once per 0.25s per actor (and then only a 5%
    /// roll plays it), so a 12-pellet shotgun blast doesn't spray a dozen overlapping ric sounds. Server-side
    /// here (the port emits impact FX server-side) rather than CSQC. Default 0.
    /// </summary>
    public float PrevRic;

    /// <summary>
    /// QC <c>.spamtime</c> (common/sounds/all.qc:122): the sim time up to which this entity is rate-limited
    /// for <c>spamsound</c> emits. A new spamsound only plays when <c>time &gt; e.spamtime</c>; on play, it is
    /// set to the current <c>time</c> so at most one spamsound fires per sim step. Used by touch handlers that
    /// can be called multiple times per frame (nade bounce, vehicle hit, monster body-impact).
    /// </summary>
    public float SpamTime;

    public Entity() { }

    public bool OnGround => (Flags & EntFlags.OnGround) != 0;

    public override string ToString() => $"{(string.IsNullOrEmpty(ClassName) ? "entity" : ClassName)}#{Index}{(IsFreed ? " (freed)" : "")}";
}
