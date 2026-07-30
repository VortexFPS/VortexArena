using System.Numerics;
using XonoticGodot.Common.Framework;
using XonoticGodot.Common.Services;

namespace XonoticGodot.Common.Gameplay;

/// <summary>
/// Additional gameplay hook chains used by the ported mutators — the C# successor to the QuakeC
/// MUTATOR_HOOKABLE events declared in server/mutators/events.qh and common/mutators/events.qh.
///
/// <see cref="GameHooks"/> already owns the sample chain (PlayerDamage_SplitHealthArmor) used by the
/// vampire mutator. This file adds the chains the rest of the batch needs, each a typed
/// <see cref="HookChain{TArgs}"/> whose <c>ref</c> args struct replaces QC's global M_ARGV(i, ...)
/// in/out slots (ADR-0003, specs/entity-model.md). Field naming follows the QC slot comments so the
/// port stays auditable.
///
/// NOTE: handlers return <c>bool</c>. For "forbid"/"return-true" QC hooks (ForbidThrowCurrentWeapon,
/// PlayerRegen, PlayerJump...) a <c>true</c> return models QC's <c>return true</c> (the hook bus ORs
/// returns together, mirroring CALLHOOK's "any handler returned true" semantics).
///
/// NOTE (dispatch/liveness): a chain is LIVE only when a matching <c>Fire*</c> wrapper is invoked on a
/// production path. Some chains are declared here for QC-parity completeness AHEAD of the subsystem that will
/// drive them (e.g. several turret / vehicle / monster events whose server or client-render side is not fully
/// ported yet), so a mutator subscribing to one of those is inert until that subsystem's <c>Fire*</c> site
/// lands. Before relying on a chain, grep for its <c>Fire*</c> — no <c>Fire*</c> caller ⇒ not yet dispatched.
/// </summary>
public static class MutatorHooks
{
    // ----------------------------------------------------------------------------------------------
    // Lifecycle / per-frame
    // ----------------------------------------------------------------------------------------------

    /// <summary>EV_PlayerSpawn — player spawned as a player (after shared setup). Slot0 player, slot1 spawn spot.</summary>
    public struct PlayerSpawnArgs
    {
        public readonly Entity Player;   // MUTATOR_ARGV_0_entity
        public readonly Entity? Spot;    // MUTATOR_ARGV_1_entity (may be null)
        public PlayerSpawnArgs(Entity player, Entity? spot) { Player = player; Spot = spot; }
    }
    public static readonly HookChain<PlayerSpawnArgs> PlayerSpawn = new();

    /// <summary>
    /// EV_MakePlayerObserver (server/mutators/events.qh) — a live player has been demoted to a free-fly OBSERVER
    /// (QC <c>MakePlayerObserver</c>, fired from <c>PutObserverInServer</c>). Mutators reset any per-player
    /// state the player must not keep while spectating (e.g. dodging_ResetPlayer, sv_dodging.qc:328). Slot0 the
    /// player being made an observer.
    /// </summary>
    public struct MakePlayerObserverArgs
    {
        public readonly Entity Player;   // MUTATOR_ARGV_0_entity
        public MakePlayerObserverArgs(Entity player) { Player = player; }
    }
    public static readonly HookChain<MakePlayerObserverArgs> MakePlayerObserver = new();

    /// <summary>
    /// EV_ClientDisconnect (server/mutators/events.qh) — a client is leaving the server (QC
    /// <c>ClientDisconnect</c>). Mutators that track the roster (e.g. dynamic_handicap recomputing the mean
    /// score across remaining players) re-run here. Slot0 the departing player. Fired from
    /// <c>ClientManager.ClientDisconnect</c> while the player is still in the roster's wake (after the gametype
    /// roster relinquish, which mirrors Base running the mode hooks before the generic mutator hook).
    /// </summary>
    public struct ClientDisconnectArgs
    {
        public readonly Entity Player;   // MUTATOR_ARGV_0_entity
        public ClientDisconnectArgs(Entity player) { Player = player; }
    }
    public static readonly HookChain<ClientDisconnectArgs> ClientDisconnect = new();

    /// <summary>
    /// EV_Player_ChangeTeam (server/mutators/events.qh) — fired from QC <c>Player_SetTeamIndex</c> BEFORE a team
    /// change is applied. A handler returning <c>true</c> BLOCKS the change (the player keeps the old team), so a
    /// mutator can veto a balance override or a per-mode team move. Slot0 the player, slot1 the old team index
    /// (1..4 or 0/-1), slot2 the requested new team index.
    /// </summary>
    public struct PlayerChangeTeamArgs
    {
        public readonly Entity Player;    // MUTATOR_ARGV_0_entity
        public readonly int OldTeamIndex; // MUTATOR_ARGV_1_int
        public readonly int NewTeamIndex; // MUTATOR_ARGV_2_int
        public PlayerChangeTeamArgs(Entity player, int oldTeamIndex, int newTeamIndex)
        { Player = player; OldTeamIndex = oldTeamIndex; NewTeamIndex = newTeamIndex; }
    }
    public static readonly HookChain<PlayerChangeTeamArgs> PlayerChangeTeam = new();

    /// <summary>
    /// EV_Player_ChangedTeam (server/mutators/events.qh) — fired from QC <c>Player_SetTeamIndex</c> AFTER a team
    /// change has been applied, so a mutator can react (per-mode side effects, stat resets). Same slots as
    /// <see cref="PlayerChangeTeam"/>; the return value is ignored.
    /// </summary>
    public struct PlayerChangedTeamArgs
    {
        public readonly Entity Player;    // MUTATOR_ARGV_0_entity
        public readonly int OldTeamIndex; // MUTATOR_ARGV_1_int
        public readonly int NewTeamIndex; // MUTATOR_ARGV_2_int
        public PlayerChangedTeamArgs(Entity player, int oldTeamIndex, int newTeamIndex)
        { Player = player; OldTeamIndex = oldTeamIndex; NewTeamIndex = newTeamIndex; }
    }
    public static readonly HookChain<PlayerChangedTeamArgs> PlayerChangedTeam = new();

    /// <summary>
    /// EV_Spawn_Score (server/mutators/events.qh) — fired from QC <c>Spawn_Score</c> (server/spawnpoints.qc)
    /// for EACH candidate spawn spot while selecting a spawnpoint, so a mutator can bias the spot's priority.
    /// Slot0 the player being spawned, slot1 the spawn spot entity, slot2 the spot's spawn-score vector
    /// (in/out — QC packs the spot's prio in <c>.x</c> and the distance weight in <c>.y</c>; handlers rewrite
    /// <see cref="Priority"/>). spawn_unique drops a repeat spot's priority to 0.1; spawn_near_teammate raises
    /// the priority of spots near a teammate. The port's scorer keeps (prio, weight) as floats, so the chain
    /// exposes both — <see cref="Priority"/> is the QC <c>spawn_score.x</c>, <see cref="Weight"/> the <c>.y</c>.
    /// </summary>
    public struct SpawnScoreArgs
    {
        public readonly Entity Player;   // MUTATOR_ARGV_0_entity
        public readonly Entity Spot;     // MUTATOR_ARGV_1_entity
        public float Priority;           // MUTATOR_ARGV_2_vector.x (in/out)
        public float Weight;             // MUTATOR_ARGV_2_vector.y (in/out)
        public SpawnScoreArgs(Entity player, Entity spot, float priority, float weight)
        {
            Player = player; Spot = spot; Priority = priority; Weight = weight;
        }
    }
    public static readonly HookChain<SpawnScoreArgs> SpawnScore = new();

    /// <summary>
    /// EV_PlayerPreThink — runs each frame for every player entity (also bots/dead/spectators).
    /// QC guards on IS_PLAYER/IS_DEAD/game_stopped inside the handler.
    /// </summary>
    public struct PlayerPreThinkArgs
    {
        public readonly Entity Player;   // MUTATOR_ARGV_0_entity
        public PlayerPreThinkArgs(Entity player) { Player = player; }
    }
    public static readonly HookChain<PlayerPreThinkArgs> PlayerPreThink = new();

    /// <summary>
    /// EV_SV_StartFrame (server/mutators/events.qh) — fired once per server frame from QC <c>StartFrame()</c>
    /// (server/main.qc), after the bot/anticheat frame and before the per-client PostThink pass. The Godot-free
    /// gameplay layer can't reach the server-core <c>ServerHooks.SvStartFrame</c> (it lives in XonoticGodot.Server,
    /// which references Common, not the other way round), so per-frame mutators that live in Common — random_gravity
    /// here — subscribe to THIS chain instead. The server's per-frame loop (<c>GameWorld.StartFrame</c> /
    /// <c>ServerHooks.FireStartFrame</c>) must also pump this chain so the handlers actually tick (see the T19
    /// report's crossTaskNeeds). Slot0 the current sim time (QC <c>time</c>).
    /// </summary>
    public struct SvStartFrameArgs
    {
        public readonly float Time;   // QC time at the top of the frame
        public SvStartFrameArgs(float time) { Time = time; }
    }
    public static readonly HookChain<SvStartFrameArgs> SvStartFrame = new();

    /// <summary>Fire <see cref="SvStartFrame"/> for the given sim time (returns true if any handler did).</summary>
    public static bool FireStartFrame(float time)
    {
        var a = new SvStartFrameArgs(time);
        return SvStartFrame.Call(ref a);
    }

    /// <summary>
    /// EV_PlayerUseKey (server/mutators/events.qh) — the +use key was pressed and was NOT consumed by the
    /// vehicle enter/exit path (QC <c>PlayerUseKey</c>, client.qc:2666: the trailing
    /// <c>MUTATOR_CALLHOOK(PlayerUseKey, this)</c> only fires when the player neither exited a seated vehicle
    /// nor boarded one). A handler returning <c>true</c> consumes the press (QC ORs the returns). The CTF flag
    /// throw/pass-request, the Keepaway/Team Keepaway/Nexball ball drop, and the KeyHunt voluntary key-drop
    /// (kh_Key_DropOne) all hang off this hook. Slot0 the player who pressed +use.
    /// </summary>
    public struct PlayerUseKeyArgs
    {
        public readonly Entity Player;   // MUTATOR_ARGV_0_entity
        public PlayerUseKeyArgs(Entity player) { Player = player; }
    }
    public static readonly HookChain<PlayerUseKeyArgs> PlayerUseKey = new();

    /// <summary>
    /// Fire <see cref="PlayerUseKey"/> for a player whose +use press fell through the vehicle path (QC
    /// <c>MUTATOR_CALLHOOK(PlayerUseKey, this)</c> at the tail of <c>PlayerUseKey</c>). Returns true if any
    /// handler consumed the press. The +use seam (<see cref="VehicleBoarding.UseKey"/>) calls this stable entry.
    /// </summary>
    public static bool FirePlayerUseKey(Entity player)
    {
        var a = new PlayerUseKeyArgs(player);
        return PlayerUseKey.Call(ref a);
    }

    /// <summary>
    /// EV_PlayerPowerups — end of player_powerups(); lets mutators tweak values set by powerup items.
    /// Slot0 player, slot1 old items bitmask.
    /// </summary>
    public struct PlayerPowerupsArgs
    {
        public readonly Entity Player;   // MUTATOR_ARGV_0_entity
        public readonly int OldItems;    // MUTATOR_ARGV_1_int
        public PlayerPowerupsArgs(Entity player, int oldItems) { Player = player; OldItems = oldItems; }
    }
    public static readonly HookChain<PlayerPowerupsArgs> PlayerPowerups = new();

    /// <summary>
    /// EV_PlayerRegen — called each think frame; a handler returning true disables regen (QC instagib).
    /// Slots 1-10 are the in/out tuning parameters that mutators may rewrite (QC client.qc:1699-1710):
    ///   max_mod   — scales the health regen/rot stable set-point (health only, not armor).
    ///   regen_mod — multiplies the regen frametime for armor+health (speed scaling).
    ///   rot_mod   — multiplies the rot frametime for armor+health (speed scaling).
    ///   limit_mod — multiplies GetResourceLimit for RotRegen's upper clamp.
    ///   regen_health / regen_health_linear / regen_health_rot / regen_health_rotlinear /
    ///   regen_health_stable / regen_health_rotstable — the six health balance values (may be overridden
    ///   by a mutator instead of the cvar reads). Default to the cvar values seeded by the caller.
    /// A return of true short-circuits the health+armor RotRegen (does NOT skip the fuel block or rot-to-death).
    /// </summary>
    public struct PlayerRegenArgs
    {
        public readonly Entity Player;   // MUTATOR_ARGV_0_entity — not rewritten
        // QC M_ARGV(1..4, float) — the four multiplier mods (in/out, default 1).
        public float MaxMod;             // MUTATOR_ARGV_1_float
        public float RegenMod;           // MUTATOR_ARGV_2_float
        public float RotMod;             // MUTATOR_ARGV_3_float
        public float LimitMod;           // MUTATOR_ARGV_4_float
        // QC M_ARGV(5..10, float) — the six health balance values (in/out, initialized from cvars).
        public float RegenHealth;            // MUTATOR_ARGV_5_float
        public float RegenHealthLinear;      // MUTATOR_ARGV_6_float
        public float RegenHealthRot;         // MUTATOR_ARGV_7_float
        public float RegenHealthRotLinear;   // MUTATOR_ARGV_8_float
        public float RegenHealthStable;      // MUTATOR_ARGV_9_float
        public float RegenHealthRotStable;   // MUTATOR_ARGV_10_float

        public PlayerRegenArgs(Entity player,
            float regenHealth, float regenHealthLinear,
            float regenHealthRot, float regenHealthRotLinear,
            float regenHealthStable, float regenHealthRotStable)
        {
            Player              = player;
            MaxMod              = 1f;
            RegenMod            = 1f;
            RotMod              = 1f;
            LimitMod            = 1f;
            RegenHealth         = regenHealth;
            RegenHealthLinear   = regenHealthLinear;
            RegenHealthRot      = regenHealthRot;
            RegenHealthRotLinear = regenHealthRotLinear;
            RegenHealthStable   = regenHealthStable;
            RegenHealthRotStable = regenHealthRotStable;
        }
    }
    public static readonly HookChain<PlayerRegenArgs> PlayerRegen = new();

    // ----------------------------------------------------------------------------------------------
    // Movement
    // ----------------------------------------------------------------------------------------------

    /// <summary>
    /// EV_PlayerPhysics — before player physics, may adjust movement vars. Slot0 player, slot1 ticrate.
    /// Used by dodging / multijump to drive their per-frame movement state machines.
    /// </summary>
    public struct PlayerPhysicsArgs
    {
        public readonly Entity Player;   // MUTATOR_ARGV_0_entity
        public readonly float TicRate;   // MUTATOR_ARGV_1_float
        public PlayerPhysicsArgs(Entity player, float ticRate) { Player = player; TicRate = ticRate; }
    }
    public static readonly HookChain<PlayerPhysicsArgs> PlayerPhysics = new();

    /// <summary>
    /// EV_WeaponRateFactor (server/mutators/events.qh) — fired from QC <c>W_WeaponRateFactor</c>
    /// (common/weapons/weapon.qh) so a mutator can scale the weapon refire/animtime factor. Slot0 the factor
    /// (in/out — the handler multiplies <see cref="Factor"/> and QC reads it back via <c>M_ARGV(0, float)</c>),
    /// slot1 the firing player. The Speed powerup multiplies by
    /// <c>g_balance_powerup_speed_attack_time_multiplier</c> (0.8 → faster) and the Speed buff by
    /// <c>g_buffs_speed_rate</c>. The base factor passed in is QC's <c>1/g_weaponratefactor</c>.
    /// </summary>
    public struct WeaponRateFactorArgs
    {
        public float Factor;            // MUTATOR_ARGV_0_float (in/out)
        public readonly Entity Player;  // MUTATOR_ARGV_1_entity
        public WeaponRateFactorArgs(float factor, Entity player) { Factor = factor; Player = player; }
    }
    public static readonly HookChain<WeaponRateFactorArgs> WeaponRateFactor = new();

    /// <summary>
    /// EV_W_PlayStrengthSound (server/mutators/events.qh) — a weapon just fired its shot sound (QC
    /// <c>W_PlayStrengthSound(player)</c>, server/weapons/common.qc:40, called from the bullet-tracing
    /// sound block and shotgun melee). The powerups mutator plays the anti-spammed SND_STRENGTH_FIRE cue
    /// when the firing player holds Strength.
    /// </summary>
    public struct WPlayStrengthSoundArgs
    {
        public readonly Entity Player;  // MUTATOR_ARGV_0_entity
        public WPlayStrengthSoundArgs(Entity player) { Player = player; }
    }
    public static readonly HookChain<WPlayStrengthSoundArgs> WPlayStrengthSound = new();

    /// <summary>
    /// EV_Bot_ForbidAttack (server/mutators/events.qh) — a bot is evaluating whether it may attack a target
    /// (QC <c>bot_shouldattack</c> tail, server/bot/default/aim.qc). A handler returning <c>true</c> FORBIDS
    /// the attack. The powerups mutator forbids attacking a player who holds Invisibility (radar/bot stealth).
    /// Slot0 the attacking bot, slot1 the candidate target.
    /// </summary>
    public struct BotForbidAttackArgs
    {
        public readonly Entity Bot;     // MUTATOR_ARGV_0_entity
        public readonly Entity Target;  // MUTATOR_ARGV_1_entity
        public BotForbidAttackArgs(Entity bot, Entity target) { Bot = bot; Target = target; }
    }
    public static readonly HookChain<BotForbidAttackArgs> BotForbidAttack = new();

    /// <summary>
    /// EV_MonsterValidTarget (common/mutators/events.qh) — a monster is evaluating a candidate target (QC
    /// <c>Monster_ValidTarget</c> tail, common/monsters/sv_monsters.qc:119). A handler returning <c>true</c>
    /// INVALIDATES the target (the monster won't acquire it). The powerups mutator invalidates a player who
    /// holds Invisibility (monster stealth). Slot0 the monster, slot1 the candidate target.
    /// </summary>
    public struct MonsterValidTargetArgs
    {
        public readonly Entity Monster; // MUTATOR_ARGV_0_entity
        public readonly Entity Target;  // MUTATOR_ARGV_1_entity
        public MonsterValidTargetArgs(Entity monster, Entity target) { Monster = monster; Target = target; }
    }
    public static readonly HookChain<MonsterValidTargetArgs> MonsterValidTarget = new();

    /// <summary>
    /// Fire <see cref="MonsterValidTarget"/> for a monster/target pair (QC
    /// <c>MUTATOR_CALLHOOK(MonsterValidTarget, this, targ)</c>). Returns true if any mutator invalidates the target.
    /// </summary>
    public static bool FireMonsterValidTarget(Entity monster, Entity target)
    {
        var a = new MonsterValidTargetArgs(monster, target);
        return MonsterValidTarget.Call(ref a);
    }

    /// <summary>
    /// EV_MonsterCheckBossFlag (server/mutators/events.qh:420-427) — "called when checking if a monster should be
    /// a miniboss". Slot0 the monster (read-only). A handler returning <c>true</c> PREVENTS the monster from
    /// becoming a miniboss (QC <c>Monster_Miniboss_Setup</c>:531 clears MONSTERFLAG_MINIBOSS and returns). No
    /// stock Xonotic mutator implements this hook (it is a dead hook in Base), but the chain is wired for parity
    /// so a custom mutator can veto miniboss status exactly as QC allows.
    /// </summary>
    public struct MonsterCheckBossFlagArgs
    {
        public readonly Entity Monster; // MUTATOR_ARGV_0_entity
        public MonsterCheckBossFlagArgs(Entity monster) { Monster = monster; }
    }
    public static readonly HookChain<MonsterCheckBossFlagArgs> MonsterCheckBossFlag = new();

    /// <summary>
    /// Fire <see cref="MonsterCheckBossFlag"/> for a monster (QC <c>MUTATOR_CALLHOOK(MonsterCheckBossFlag, this)</c>
    /// in <c>Monster_Miniboss_Setup</c>:531). Returns true if any mutator vetoes miniboss status.
    /// </summary>
    public static bool FireMonsterCheckBossFlag(Entity monster)
    {
        var a = new MonsterCheckBossFlagArgs(monster);
        return MonsterCheckBossFlag.Call(ref a);
    }

    /// <summary>
    /// EV_MonsterDropItem (server/mutators/events.qh:383-390) — "called when a monster is dropping loot".
    /// Slot0 the monster (read-only), slot1 the item-list string (in/out — a handler may replace it to override
    /// what loot is dropped; setting it to "" suppresses the drop entirely), slot2 the attacker (read-only).
    /// Mirrors QC <c>MUTATOR_CALLHOOK(MonsterDropItem, this, itemlist, attacker)</c> in <c>monster_dropitem</c>
    /// (sv_monsters.qc:51-52): the updated M_ARGV(1) is used as the item list after the hook returns.
    /// </summary>
    public struct MonsterDropItemArgs
    {
        public readonly Entity Monster;   // MUTATOR_ARGV_0_entity
        public string ItemList;           // MUTATOR_ARGV_1_string (in/out)
        public readonly Entity? Attacker; // MUTATOR_ARGV_2_entity
        public MonsterDropItemArgs(Entity monster, string itemList, Entity? attacker)
        { Monster = monster; ItemList = itemList; Attacker = attacker; }
    }
    public static readonly HookChain<MonsterDropItemArgs> MonsterDropItem = new();

    /// <summary>
    /// Fire <see cref="MonsterDropItem"/> before the monster loot is resolved (QC
    /// <c>MUTATOR_CALLHOOK(MonsterDropItem, this, itemlist, attacker); itemlist = M_ARGV(1, string);</c>).
    /// Returns the (possibly overridden) item-list string; empty string suppresses the drop.
    /// </summary>
    public static string FireMonsterDropItem(Entity monster, string itemList, Entity? attacker)
    {
        var a = new MonsterDropItemArgs(monster, itemList, attacker);
        MonsterDropItem.Call(ref a);
        return a.ItemList;
    }

    /// <summary>
    /// EV_MonsterSpawn (server/mutators/events.qh:357-361) — "called when a monster spawns". Slot0 the monster
    /// entity (read/write — handlers may modify fields like .skin). A handler returning <c>true</c> cancels the
    /// spawn (QC: <c>if (MUTATOR_CALLHOOK(MonsterSpawn, this)) return false;</c> in Monster_Spawn, line 1381).
    /// </summary>
    public struct MonsterSpawnArgs
    {
        public readonly Entity Monster; // MUTATOR_ARGV_0_entity
        public MonsterSpawnArgs(Entity monster) { Monster = monster; }
    }
    public static readonly HookChain<MonsterSpawnArgs> MonsterSpawn = new();

    /// <summary>
    /// Fire <see cref="MonsterSpawn"/> just after the monster is fully set up (QC
    /// <c>if (MUTATOR_CALLHOOK(MonsterSpawn, this)) return false;</c>). Returns true if any handler cancels
    /// the spawn (causing <see cref="MonsterAI.SpawnFromMap"/> to remove the monster and return false).
    /// </summary>
    public static bool FireMonsterSpawn(Entity monster)
    {
        var a = new MonsterSpawnArgs(monster);
        return MonsterSpawn.Call(ref a);
    }

    /// <summary>
    /// Fire <see cref="BotForbidAttack"/> for a bot/target pair (QC
    /// <c>MUTATOR_CALLHOOK(Bot_ForbidAttack, this, targ)</c>). Returns true if any mutator forbids the attack.
    /// </summary>
    public static bool FireBotForbidAttack(Entity bot, Entity target)
    {
        var a = new BotForbidAttackArgs(bot, target);
        return BotForbidAttack.Call(ref a);
    }

    /// <summary>
    /// Fire <see cref="WPlayStrengthSound"/> for a player who just fired a weapon's shot sound (QC
    /// <c>W_PlayStrengthSound</c>).
    /// </summary>
    public static void FireWPlayStrengthSound(Entity player)
    {
        var a = new WPlayStrengthSoundArgs(player);
        WPlayStrengthSound.Call(ref a);
    }

    /// <summary>
    /// EV_PlayerJump — player pressed jump. Slot0 player, slot1 jump height (in/out), slot2 multijump
    /// flag (in/out). Bloodloss returns true to forbid the jump; multijump/walljump set
    /// <see cref="Multijump"/> = true to grant an extra jump.
    /// </summary>
    public struct PlayerJumpArgs
    {
        public readonly Entity Player;   // MUTATOR_ARGV_0_entity
        public float JumpHeight;         // MUTATOR_ARGV_1_float (in/out)
        public bool Multijump;           // MUTATOR_ARGV_2_bool  (in/out)
        // Per-player resolved DOUBLEJUMP stat (QC PHYS_DOUBLEJUMP(player) ==
        // Physics_ClientOption(this, "doublejump", autocvar_sv_doublejump)). Carried from the physics call
        // site (MovementParameters.DoubleJump) so the doublejump mutator gates its grant on the PER-PLAYER
        // value — which can differ from the raw sv_doublejump cvar when g_physics_clientselect is on.
        // null = not supplied (e.g. unit tests using the 3-arg ctor) → the mutator falls back to the raw
        // sv_doublejump cvar, which equals the stat in stock play (g_physics_clientselect 0).
        public bool? DoubleJump;
        // True on the CLIENT-PREDICTION leg (QC: hook runs in CSQC). Mutators that #ifdef SVQC their
        // side-effects (e.g. walljump's smoke ring / jump voice / LASTWJ + oldvelocity stamps,
        // walljump.qc:62-68) gate those on !Predicted so the predicting client runs ONLY the shared
        // velocity impulse — exactly as Base's CSQC build does — and does not double the server's
        // networked effect/sound. Defaults false (the server/authoritative leg) so existing 3/4-arg
        // callers and headless tests retain the full server behavior.
        public bool Predicted;
        public PlayerJumpArgs(Entity player, float jumpHeight, bool multijump)
        {
            Player = player; JumpHeight = jumpHeight; Multijump = multijump; DoubleJump = null; Predicted = false;
        }
        public PlayerJumpArgs(Entity player, float jumpHeight, bool multijump, bool doubleJump)
        {
            Player = player; JumpHeight = jumpHeight; Multijump = multijump; DoubleJump = doubleJump; Predicted = false;
        }
    }
    public static readonly HookChain<PlayerJumpArgs> PlayerJump = new();

    /// <summary>
    /// EV_PlayerCanCrouch — decides whether a player can crouch. Slot0 player, slot1 do_crouch (in/out).
    /// Bloodloss forces do_crouch=true when the player is below the bloodloss threshold.
    /// </summary>
    public struct PlayerCanCrouchArgs
    {
        public readonly Entity Player;   // MUTATOR_ARGV_0_entity
        public bool DoCrouch;            // MUTATOR_ARGV_1_bool (in/out)
        public PlayerCanCrouchArgs(Entity player, bool doCrouch) { Player = player; DoCrouch = doCrouch; }
    }
    public static readonly HookChain<PlayerCanCrouchArgs> PlayerCanCrouch = new();

    // Port of qcsrc/common/mutators/events.qh (EV_PM_Physics, EV_IsFlying).

    /// <summary>
    /// EV_PM_Physics (events.qh:93-98) — "called during player physics, allows adjusting the movement type
    /// used". Fired in <c>sys_phys_update</c> (ecs/systems/physics.qc:108) as the SECOND branch of the
    /// movement-type selection (right after the FL_WATERJUMP branch): a handler that returns <c>true</c>
    /// has FULLY REPLACED the move, so the rest of the branch chain (noclip/fly/swim/ladder/jetpack/ground/
    /// air) is skipped and only the post-update bookkeeping runs. This is the hook bugrigs (vehicle-style
    /// drive physics) hangs off. Slot0 player, slot1 maxspeed_mod, slot2 tick rate (dt) — all <c>in</c>,
    /// no <c>out</c> slots (the handler mutates the player's velocity/origin directly).
    /// </summary>
    public struct PMPhysicsArgs
    {
        public readonly Entity Player;     // MUTATOR_ARGV_0_entity
        public readonly float MaxspeedMod; // MUTATOR_ARGV_1_float
        public readonly float TicRate;     // MUTATOR_ARGV_2_float
        public PMPhysicsArgs(Entity player, float maxspeedMod, float ticRate)
        {
            Player = player; MaxspeedMod = maxspeedMod; TicRate = ticRate;
        }
    }
    public static readonly HookChain<PMPhysicsArgs> PMPhysics = new();

    /// <summary>
    /// EV_IsFlying (events.qh:58-61) — the LAST term of the <c>||</c> that selects the noclip/fly branch in
    /// <c>sys_phys_update</c> (physics.qc:113: <c>MOVETYPE_NOCLIP || MOVETYPE_FLY || MOVETYPE_FLY_WORLDONLY
    /// || MUTATOR_CALLHOOK(IsFlying, this)</c>). A handler returning <c>true</c> FORCES the fly branch (air-
    /// friction half-step + full-3D PM_Accelerate, no gravity). Slot0 player only, no <c>out</c> slots.
    /// NOTE: this MUTATOR_HOOKABLE(IsFlying) is DISTINCT from the plain <c>bool IsFlying(entity)</c> function
    /// (common/physics/player.qc:843, the onground/swimming/24u airshot test used for .wasFlying and by
    /// weapons) — only the hook is ported here.
    /// </summary>
    public struct IsFlyingArgs
    {
        public readonly Entity Player;   // MUTATOR_ARGV_0_entity
        public IsFlyingArgs(Entity player) { Player = player; }
    }
    public static readonly HookChain<IsFlyingArgs> IsFlying = new();

    // ----------------------------------------------------------------------------------------------
    // Damage / death
    // ----------------------------------------------------------------------------------------------

    /// <summary>
    /// EV_Damage_Calculate — adjusts the damage/force/mirror-damage applied to a target (QC the place
    /// strength multipliers, instagib lives, midair multipliers, overkill blaster nullification etc.
    /// live). All of <see cref="Damage"/>, <see cref="MirrorDamage"/>, <see cref="Force"/> are in/out.
    /// </summary>
    public struct DamageCalculateArgs
    {
        public readonly Entity? Inflictor;   // MUTATOR_ARGV_0_entity
        public readonly Entity? Attacker;    // MUTATOR_ARGV_1_entity
        public readonly Entity Target;       // MUTATOR_ARGV_2_entity
        public readonly string DeathType;    // MUTATOR_ARGV_3_float (deathtype tag — string in this port)
        public float Damage;                 // MUTATOR_ARGV_4_float (in/out)
        public float MirrorDamage;           // MUTATOR_ARGV_5_float (in/out)
        public Vector3 Force;                // MUTATOR_ARGV_6_vector (in/out)
        public readonly Entity? WeaponEntity; // MUTATOR_ARGV_7_entity

        public DamageCalculateArgs(Entity? inflictor, Entity? attacker, Entity target, string deathType,
            float damage, float mirrorDamage, Vector3 force, Entity? weaponEntity)
        {
            Inflictor = inflictor;
            Attacker = attacker;
            Target = target;
            DeathType = deathType;
            Damage = damage;
            MirrorDamage = mirrorDamage;
            Force = force;
            WeaponEntity = weaponEntity;
        }
    }
    public static readonly HookChain<DamageCalculateArgs> DamageCalculate = new();

    /// <summary>
    /// EV_PlayerDies — a player died; lets mutators drop carried stuff / force gibbing. Slot0 inflictor,
    /// slot1 attacker, slot2 target, slot3 deathtype, slot4 damage (in/out — instagib bumps it to 1000
    /// to always gib on a vaporizer kill). Pinata/overkill throw weapons here.
    /// </summary>
    public struct PlayerDiesArgs
    {
        public readonly Entity? Inflictor;  // MUTATOR_ARGV_0_entity
        public readonly Entity? Attacker;   // MUTATOR_ARGV_1_entity
        public readonly Entity Target;      // MUTATOR_ARGV_2_entity
        public readonly string DeathType;   // MUTATOR_ARGV_3_float (deathtype tag — string in this port)
        public float Damage;                // MUTATOR_ARGV_4_float (in/out)

        public PlayerDiesArgs(Entity? inflictor, Entity? attacker, Entity target, string deathType, float damage)
        {
            Inflictor = inflictor;
            Attacker = attacker;
            Target = target;
            DeathType = deathType;
            Damage = damage;
        }
    }
    public static readonly HookChain<PlayerDiesArgs> PlayerDies = new();

    /// <summary>
    /// EV_GiveFragsForKill (server/mutators/events.qh) — fired from QC <c>GiveFrags</c> (server/damage.qc:72)
    /// when "self" fragged someone, so a mutator can change the frag score awarded for the kill. Slot0
    /// attacker, slot1 target, slot2 frag score (in/out — the handler rewrites <see cref="FragScore"/> and QC
    /// reads it back via <c>f = M_ARGV(2, float)</c>), slot3 deathtype, slot4 the attacker's weapon entity.
    /// weaponarena_random/instagib adjust the score here.
    /// </summary>
    public struct GiveFragsForKillArgs
    {
        public readonly Entity Attacker;      // MUTATOR_ARGV_0_entity
        public readonly Entity Target;        // MUTATOR_ARGV_1_entity
        public float FragScore;               // MUTATOR_ARGV_2_float (in/out)
        public readonly string DeathType;     // MUTATOR_ARGV_3_float (deathtype tag — string in this port)
        public readonly Entity? WeaponEntity; // MUTATOR_ARGV_4_entity

        public GiveFragsForKillArgs(Entity attacker, Entity target, float fragScore, string deathType, Entity? weaponEntity)
        {
            Attacker = attacker;
            Target = target;
            FragScore = fragScore;
            DeathType = deathType;
            WeaponEntity = weaponEntity;
        }
    }
    public static readonly HookChain<GiveFragsForKillArgs> GiveFragsForKill = new();

    /// <summary>
    /// EV_FragCenterMessage (server/mutators/events.qh) — fired from QC <c>Obituary</c> (server/damage.qc:371,
    /// just before the frag/typefrag centerprints) so a gametype can REPLACE or SUPPRESS the per-kill personal
    /// centerprint sent to the attacker ("You fragged X") and the victim ("You were fragged by Y"). Freeze Tag
    /// swaps these for CHOICE_FRAG_FREEZE / CHOICE_FRAGGED_FREEZE ("You froze X" / "You were frozen by Y"), and
    /// suppresses them entirely when the target was ALREADY frozen (re-killed in void/lava). Slot0 attacker,
    /// slot1 target, slot2 deathtype; the in/out <see cref="AttackerChoice"/>/<see cref="TargetChoice"/> carry
    /// the CHOICE name the obituary should send to each (default "FRAG"/"FRAGGED"), and <see cref="Suppress"/>
    /// drops both lines. A handler returning <c>true</c> marks the message handled (QC's return-true contract);
    /// the obituary then honours the (possibly-rewritten) slots.
    /// </summary>
    public struct FragCenterMessageArgs
    {
        public readonly Entity Attacker;   // MUTATOR_ARGV_0_entity
        public readonly Entity Target;      // MUTATOR_ARGV_1_entity
        public readonly string DeathType;   // the kill's deathtype tag
        public string AttackerChoice;       // in/out: the CHOICE_* name sent to the attacker (default "FRAG")
        public string TargetChoice;         // in/out: the CHOICE_* name sent to the target   (default "FRAGGED")
        public bool Suppress;               // in/out: drop both centerprints entirely (already-frozen re-kill)

        public FragCenterMessageArgs(Entity attacker, Entity target, string deathType)
        {
            Attacker = attacker;
            Target = target;
            DeathType = deathType;
            AttackerChoice = "FRAG";
            TargetChoice = "FRAGGED";
            Suppress = false;
        }
    }
    public static readonly HookChain<FragCenterMessageArgs> FragCenterMessage = new();

    /// <summary>
    /// EV_PreferPlayerScore_Clear (server/mutators/events.qh) — fired from <c>Score_ClearOnJoin</c> when a
    /// player rejoins after going observer and <c>g_score_resetonjoin == -1</c> (admin config: rejoin clears
    /// score unless a mutator vetoes). A handler returning <c>true</c> VETOES the score wipe — the gametype
    /// prefers to KEEP the player's accumulated score. TKA returns true (the distinguishing rule for Team
    /// Keepaway is persistent team score across specs). Slot0 the rejoining player (Entity).
    /// </summary>
    public struct PreferPlayerScore_ClearArgs
    {
        public readonly Entity Player;   // MUTATOR_ARGV_0_entity
        public PreferPlayerScore_ClearArgs(Entity player) { Player = player; }
    }
    public static readonly HookChain<PreferPlayerScore_ClearArgs> PreferPlayerScore_Clear = new();

    /// <summary>
    /// Fire <see cref="PreferPlayerScore_Clear"/> for a rejoining player and return true if any handler
    /// vetoes the score wipe (QC <c>MUTATOR_CALLHOOK(PreferPlayerScore_Clear, player)</c>).
    /// </summary>
    public static bool FirePreferPlayerScore_Clear(Entity player)
    {
        var a = new PreferPlayerScore_ClearArgs(player);
        return PreferPlayerScore_Clear.Call(ref a);
    }

    /// <summary>
    /// EV_PlayHitsound (server/mutators/events.qh:124) — a player inflicted damage on a victim; a handler
    /// returning <c>true</c> FORCES a hitsound to play for the attacker (QC <c>MUTATOR_CALLHOOK(PlayHitsound,
    /// victim, attacker)</c>, server/damage.qc:632, ORed with the player/turret/monster victim test). Assault
    /// returns true for a <c>func_assault_destructible</c> victim to ensure audible feedback when shelling walls.
    /// Slot0 the damage victim, slot1 the attacker.
    ///
    /// NOTE: the port has no live firing site — its hitsound stat (<c>HitsoundDamageDealtTotal</c>,
    /// DamageSystem.Apply) already accumulates for any non-self target a player attacker hits, so a
    /// func_assault_destructible already plays the hitsound without this hook. The hook is modeled for
    /// registry-coverage parity (it would only become load-bearing if the port ever gated the stat on the
    /// victim type, as Base does).
    /// </summary>
    public struct PlayHitsoundArgs
    {
        public readonly Entity Victim;    // MUTATOR_ARGV_0_entity
        public readonly Entity? Attacker; // MUTATOR_ARGV_1_entity
        public PlayHitsoundArgs(Entity victim, Entity? attacker = null) { Victim = victim; Attacker = attacker; }
    }
    public static readonly HookChain<PlayHitsoundArgs> PlayHitsound = new();

    /// <summary>
    /// Fire <see cref="PlayHitsound"/> for a damage victim/attacker pair; returns true if any handler forces a
    /// hitsound (QC <c>MUTATOR_CALLHOOK(PlayHitsound, victim, attacker)</c>).
    /// </summary>
    public static bool FirePlayHitsound(Entity victim, Entity? attacker = null)
    {
        var a = new PlayHitsoundArgs(victim, attacker);
        return PlayHitsound.Call(ref a);
    }

    // ----------------------------------------------------------------------------------------------
    // Items / loadout / arena
    // ----------------------------------------------------------------------------------------------

    /// <summary>
    /// EV_SetStartItems (no QC args) — adjusts the {warmup_}start_* loadout globals. Modeled as a
    /// mutable <see cref="StartLoadout"/> the handlers write (the C# successor to QC's start_weapons /
    /// start_ammo_* / start_health globals).
    /// </summary>
    public struct SetStartItemsArgs
    {
        public StartLoadout Loadout;
        public SetStartItemsArgs(StartLoadout loadout) { Loadout = loadout; }
    }
    public static readonly HookChain<SetStartItemsArgs> SetStartItems = new();

    /// <summary>
    /// EV_SetWeaponArena — string in/out. Mutators set it to "off" to disable any configured weapon
    /// arena (instagib/overkill/melee/nix all do this).
    /// </summary>
    public struct SetWeaponArenaArgs
    {
        public string Arena;   // MUTATOR_ARGV_0_string (in/out)
        public SetWeaponArenaArgs(string arena) { Arena = arena; }
    }
    public static readonly HookChain<SetWeaponArenaArgs> SetWeaponArena = new();

    /// <summary>
    /// Fire <see cref="SetWeaponArena"/> for the given starting arena string and return the resolved arena
    /// (QC <c>MUTATOR_CALLHOOK(SetWeaponArena, "")</c> then reads back <c>M_ARGV(0, string)</c>). The arena
    /// hooks rewrite the in/out slot to "off" to disable a configured weapon arena (instagib/overkill/melee/
    /// nix) or to a named pool ("most"/"all"/...). The owner of the start-items path (SpawnSystem) calls this
    /// stable entry point instead of reconstructing the args struct, so the chain signature stays fixed here.
    /// </summary>
    public static string FireSetWeaponArena(string arena)
    {
        var a = new SetWeaponArenaArgs(arena);
        SetWeaponArena.Call(ref a);
        return a.Arena;
    }

    /// <summary>
    /// EV_SetWeaponreplace (server/mutators/events.qh) — fired from QC <c>weapon_defaultspawnfunc</c>
    /// (server/weapons/spawning.qc:43) for each <c>weapon_*</c> map entity, BEFORE the regular weaponreplace
    /// resolves: <c>MUTATOR_CALLHOOK(SetWeaponreplace, this, wpn, s)</c>. Slot0 the spawning world item entity
    /// (carries the map <c>"new_toys"</c> key via <see cref="Entity.NewToys"/>), slot1 the weapon being spawned,
    /// slot2 the space-joined replacement token list (in/out — a handler rewrites <see cref="Replacement"/> and
    /// the spawner reads it back). New Toys rewrites it to the map key / auto mapping; the spawner then tokenizes
    /// the result and spawns the resulting weapon(s).
    /// </summary>
    public struct SetWeaponreplaceArgs
    {
        public readonly Entity Item;        // MUTATOR_ARGV_0_entity (the spawning world item)
        public readonly Weapon Weapon;      // MUTATOR_ARGV_1_entity (the weapon being spawned)
        public string Replacement;          // MUTATOR_ARGV_2_string (in/out)
        public SetWeaponreplaceArgs(Entity item, Weapon weapon, string replacement)
        {
            Item = item; Weapon = weapon; Replacement = replacement;
        }
    }
    public static readonly HookChain<SetWeaponreplaceArgs> SetWeaponreplace = new();

    /// <summary>
    /// Fire <see cref="SetWeaponreplace"/> for a spawning weapon item and return the resolved replacement token
    /// list (QC <c>MUTATOR_CALLHOOK(SetWeaponreplace, this, wpn, s); s = M_ARGV(2, string);</c>). The weapon
    /// spawn path (<c>ItemSpawnFuncs.WeaponSpawn</c>) calls this stable entry point; New Toys' handler rewrites
    /// the list per its map key / autoreplace mapping.
    /// </summary>
    public static string FireSetWeaponreplace(Entity item, Weapon weapon, string replacement)
    {
        var a = new SetWeaponreplaceArgs(item, weapon, replacement);
        SetWeaponreplace.Call(ref a);
        return a.Replacement;
    }

    /// <summary>
    /// EV_ForbidRandomStartWeapons — return true to forbid giving random start weapons (slot0 player).
    /// </summary>
    public struct ForbidRandomStartWeaponsArgs
    {
        public readonly Entity Player;   // MUTATOR_ARGV_0_entity
        public ForbidRandomStartWeaponsArgs(Entity player) { Player = player; }
    }
    public static readonly HookChain<ForbidRandomStartWeaponsArgs> ForbidRandomStartWeapons = new();

    /// <summary>
    /// Fire <see cref="ForbidRandomStartWeapons"/> for the spawning player; returns true if any handler
    /// forbids giving random start weapons (QC <c>if (MUTATOR_CALLHOOK(ForbidRandomStartWeapons, player))
    /// return;</c>). instagib/overkill/melee_only/nix all return true. The owner of the start-items path
    /// (SpawnSystem) calls this stable entry point.
    /// </summary>
    public static bool FireForbidRandomStartWeapons(Entity player)
    {
        var a = new ForbidRandomStartWeaponsArgs(player);
        return ForbidRandomStartWeapons.Call(ref a);
    }

    /// <summary>
    /// EV_ForbidThrowCurrentWeapon — return true to forbid dropping the current weapon. Slot0 player,
    /// slot1 weapon entity (deferred). Many arena-style mutators forbid throwing.
    /// </summary>
    public struct ForbidThrowCurrentWeaponArgs
    {
        public readonly Entity Player;   // MUTATOR_ARGV_0_entity
        public ForbidThrowCurrentWeaponArgs(Entity player) { Player = player; }
    }
    public static readonly HookChain<ForbidThrowCurrentWeaponArgs> ForbidThrowCurrentWeapon = new();

    /// <summary>
    /// EV_FilterItemDefinition — return true to disallow items of the given definition from spawning.
    /// Slot0 the item definition entity (modeled loosely by ClassName / NetName until the item registry
    /// lands). NIX/melee filter health+armor here.
    /// </summary>
    public struct FilterItemDefinitionArgs
    {
        public readonly Entity Definition;   // MUTATOR_ARGV_0_entity
        public FilterItemDefinitionArgs(Entity definition) { Definition = definition; }
    }
    public static readonly HookChain<FilterItemDefinitionArgs> FilterItemDefinition = new();

    /// <summary>
    /// EV_Item_ScheduleRespawn (server/mutators/events.qh) — fired from QC <c>Item_ScheduleRespawnIn</c>
    /// (server/items/items.qc:329): <c>set_itemstime || MUTATOR_CALLHOOK(Item_ScheduleRespawn, e, t)</c>. A
    /// handler returning <c>true</c> forces the item onto the visible respawn-countdown (waypoint) path for a
    /// long respawn even when the item isn't a default itemstime item. Slot0 the item entity, slot1 the
    /// scheduled respawn time. Overkill subscribes here so the surviving Mega/Big health/armor get a countdown
    /// waypoint.
    /// </summary>
    public struct ItemScheduleRespawnArgs
    {
        public readonly Entity Item;   // MUTATOR_ARGV_0_entity
        public readonly float Time;    // MUTATOR_ARGV_1_float
        public ItemScheduleRespawnArgs(Entity item, float time) { Item = item; Time = time; }
    }
    public static readonly HookChain<ItemScheduleRespawnArgs> ItemScheduleRespawn = new();

    /// <summary>
    /// Fire <see cref="ItemScheduleRespawn"/> for an item being scheduled (QC
    /// <c>MUTATOR_CALLHOOK(Item_ScheduleRespawn, e, t)</c>); returns true if a handler wants the visible
    /// countdown-waypoint path. The item-respawn owner (<c>ItemPickupRules.ScheduleRespawnIn</c>) calls this.
    /// </summary>
    public static bool FireItemScheduleRespawn(Entity item, float time)
    {
        var a = new ItemScheduleRespawnArgs(item, time);
        return ItemScheduleRespawn.Call(ref a);
    }

    /// <summary>
    /// EV_Item_RespawnCountdown (server/mutators/events.qh) — fired from QC <c>Item_RespawnCountdown</c>
    /// (server/items/items.qc:289) on the first countdown tick, after the waypoint is spawned. A handler
    /// returning <c>true</c> suppresses the spectator-only waypoint rule (QC: the
    /// <c>Item_ItemsTime_SpectatorOnly(def) &amp;&amp; !mutator_returnvalue</c> guard) so the countdown waypoint
    /// shows for ALL players, not just spectators. Slot0 the item entity. Overkill subscribes here so the
    /// surviving Mega/Big health/armor waypoints are visible to everyone.
    /// </summary>
    public struct ItemRespawnCountdownArgs
    {
        public readonly Entity Item;   // MUTATOR_ARGV_0_entity
        public ItemRespawnCountdownArgs(Entity item) { Item = item; }
    }
    public static readonly HookChain<ItemRespawnCountdownArgs> ItemRespawnCountdown = new();

    /// <summary>
    /// Fire <see cref="ItemRespawnCountdown"/> for an item starting its countdown (QC
    /// <c>MUTATOR_CALLHOOK(Item_RespawnCountdown, this)</c>); returns true if a handler wants the waypoint
    /// shown to everyone (not spectator-only). The item-respawn owner (<c>ItemPickupRules.RespawnCountdown</c>)
    /// calls this on the first countdown tick.
    /// </summary>
    public static bool FireItemRespawnCountdown(Entity item)
    {
        var a = new ItemRespawnCountdownArgs(item);
        return ItemRespawnCountdown.Call(ref a);
    }

    /// <summary>
    /// EV_OnEntityPreSpawn (server/mutators/events.qh) — fired from QC's <c>SV_OnEntityPreSpawnFunction</c> for
    /// every parsed map entity BEFORE its spawnfunc runs; a handler returning <c>true</c> DELETES the entity
    /// (QC <c>if (MUTATOR_CALLHOOK(OnEntityPreSpawn, this)) { delete(this); return; }</c>). Slot0 the map
    /// entity. NIX subscribes here to drop <c>target_items</c> triggers (they would otherwise change weapons/
    /// ammo and fight the rotation).
    /// </summary>
    public struct OnEntityPreSpawnArgs
    {
        public readonly Entity Entity;   // MUTATOR_ARGV_0_entity
        public OnEntityPreSpawnArgs(Entity entity) { Entity = entity; }
    }
    public static readonly HookChain<OnEntityPreSpawnArgs> OnEntityPreSpawn = new();

    /// <summary>
    /// Fire <see cref="OnEntityPreSpawn"/> for a map entity about to be spawned; returns true if a handler
    /// wants it DELETED (QC <c>MUTATOR_CALLHOOK(OnEntityPreSpawn, this)</c>). The map-entity spawn loop owner
    /// (<c>GameWorld.SpawnMapEntities</c>) calls this stable entry point before dispatching the spawnfunc.
    /// </summary>
    public static bool FireOnEntityPreSpawn(Entity entity)
    {
        var a = new OnEntityPreSpawnArgs(entity);
        return OnEntityPreSpawn.Call(ref a);
    }

    /// <summary>
    /// EV_ItemTouch (server/mutators/events.qh) — fired from QC <c>Item_Touch</c> (server/items/items.qc:706),
    /// AFTER the touch gate (FL_PICKUPITEMS / alive / SOLID_TRIGGER / owner / spawnshield) and BEFORE the
    /// expiring-timer adjust + give, so a handler can react to a player about to collect a world item while the
    /// item still carries its raw powerup timers (<see cref="Entity.StrengthFinished"/> /
    /// <see cref="Entity.InvincibleFinished"/>). Slot0 the item entity, slot1 the toucher (the picking player).
    /// QC returns a <c>MUT_ITEMTOUCH_*</c> code (CONTINUE/RETURN/PICKUP); the only stock subscriber (superspec)
    /// always returns CONTINUE (never blocks the pickup), so the port models this as a notify-style chain (the
    /// bool return is ignored by the item path).
    /// </summary>
    public struct ItemTouchArgs
    {
        public readonly Entity Item;      // MUTATOR_ARGV_0_entity
        public readonly Entity Toucher;   // MUTATOR_ARGV_1_entity
        public ItemTouchArgs(Entity item, Entity toucher) { Item = item; Toucher = toucher; }
    }
    public static readonly HookChain<ItemTouchArgs> ItemTouch = new();

    /// <summary>
    /// Fire <see cref="ItemTouch"/> for a player collecting a world item (QC <c>MUTATOR_CALLHOOK(ItemTouch,
    /// this, toucher)</c>). The item-pickup owner (<c>ItemPickupRules.ItemTouch</c>) calls this stable entry
    /// point at the same point in the gate the QC switch sits. The stock subscriber (superspec) never blocks
    /// the pickup, so the return is informational only.
    /// </summary>
    public static bool FireItemTouch(Entity item, Entity toucher)
    {
        var a = new ItemTouchArgs(item, toucher);
        return ItemTouch.Call(ref a);
    }

    /// <summary>
    /// EV_FilterItem (server/mutators/events.qh) — fired from QC <c>StartItem</c> (server/items/items.qc:1031:
    /// <c>if (MUTATOR_CALLHOOK(FilterItem, this)) { delete(this); return; }</c>), AFTER the items/weapon/flags
    /// seeding and BEFORE the have-pickup gate. DISTINCT from <see cref="FilterItemDefinition"/>: this is the
    /// ENTITY-level hook that can REPLACE a map item with a different classname (random_items) — its CBC_ORDER_LAST
    /// subscriber spawns a fresh replacement item from the live edict's origin/spawnflags and returns <c>true</c>
    /// so the original is deleted. A <c>true</c> return DELETES the spawning item. Slot0 the spawning item entity.
    /// </summary>
    public struct FilterItemArgs
    {
        public readonly Entity Item;   // MUTATOR_ARGV_0_entity
        public FilterItemArgs(Entity item) { Item = item; }
    }
    public static readonly HookChain<FilterItemArgs> FilterItem = new();

    /// <summary>
    /// Fire <see cref="FilterItem"/> for an item about to go live (QC <c>MUTATOR_CALLHOOK(FilterItem, this)</c>);
    /// returns true if a handler wants the item DELETED (it has already spawned its replacement). The world-item
    /// spawn driver (<see cref="StartItem"/>) calls this stable entry point at the same seam as the QC hook.
    /// </summary>
    public static bool FireFilterItem(Entity item)
    {
        var a = new FilterItemArgs(item);
        return FilterItem.Call(ref a);
    }

    /// <summary>
    /// EV_ItemTouched (server/mutators/events.qh) — fired from QC <c>Item_Touch</c> (server/items/items.qc:746),
    /// AFTER a successful give + pickup sound (LABEL pickup), so a handler can re-spawn / re-randomize the item.
    /// DISTINCT from <see cref="ItemTouch"/> (which fires before the give): this fires after pickup. The
    /// random_items CBC_ORDER_LAST subscriber replaces the touched map item, schedules the replacement's respawn,
    /// and deletes the original — so the item re-randomizes on each respawn. A handler may free the item; the
    /// caller re-checks <see cref="Entity.Removed"/> after firing. Slot0 the item entity, slot1 the toucher.
    /// </summary>
    public struct ItemTouchedArgs
    {
        public readonly Entity Item;      // MUTATOR_ARGV_0_entity
        public readonly Entity Toucher;   // MUTATOR_ARGV_1_entity
        public ItemTouchedArgs(Entity item, Entity toucher) { Item = item; Toucher = toucher; }
    }
    public static readonly HookChain<ItemTouchedArgs> ItemTouched = new();

    /// <summary>
    /// Fire <see cref="ItemTouched"/> after a world item was picked up (QC <c>MUTATOR_CALLHOOK(ItemTouched, this,
    /// toucher)</c>). Notify-style return; the item-pickup owner (<see cref="ItemPickupRules.ItemTouch"/>) calls
    /// this then re-checks whether the item was freed (random_items deletes + re-spawns it).
    /// </summary>
    public static bool FireItemTouched(Entity item, Entity toucher)
    {
        var a = new ItemTouchedArgs(item, toucher);
        return ItemTouched.Call(ref a);
    }

    /// <summary>
    /// EV_RandomItems_GetRandomItemClassName (common/mutators/mutator/random_items/sv_random_items.qh:41) — the
    /// mod-injection hook fired at the entry of QC <c>RandomItems_GetRandomItemClassName(prefix)</c>
    /// (sv_random_items.qc:56): a mod (Overkill / Instagib) consumes it to substitute its OWN item pool. Slot0 the
    /// probability-cvar prefix (<c>in</c>), slot1 the chosen classname (<c>in/out</c> — a handler returning
    /// <c>true</c> sets <see cref="ClassName"/> and that classname is returned instead of the vanilla pick).
    /// </summary>
    public struct RandomItemsClassNameArgs
    {
        public readonly string Prefix;   // MUTATOR_ARGV_0_string
        public string ClassName;         // MUTATOR_ARGV_1_string (in/out)
        public RandomItemsClassNameArgs(string prefix) { Prefix = prefix; ClassName = ""; }
    }
    public static readonly HookChain<RandomItemsClassNameArgs> RandomItemsGetClassName = new();

    /// <summary>
    /// Fire <see cref="RandomItemsGetClassName"/> for a random-items prefix; returns the overriding classname (or
    /// <c>null</c> if no handler consumed it, so the caller falls through to the vanilla pick). QC:
    /// <c>if (MUTATOR_CALLHOOK(RandomItems_GetRandomItemClassName, prefix)) return M_ARGV(1, string);</c>.
    /// </summary>
    public static string? FireRandomItemsGetClassName(string prefix)
    {
        var a = new RandomItemsClassNameArgs(prefix);
        return RandomItemsGetClassName.Call(ref a) ? a.ClassName : null;
    }

    /// <summary>
    /// EV_EditProjectile — lets mutators edit a just-fired projectile. Slot0 owner, slot1 projectile.
    /// (invincibleproj zeroes the projectile's health; rocketflying clears detonate delays.)
    /// </summary>
    public struct EditProjectileArgs
    {
        public readonly Entity? Owner;       // MUTATOR_ARGV_0_entity
        public readonly Entity Projectile;   // MUTATOR_ARGV_1_entity
        public EditProjectileArgs(Entity? owner, Entity projectile) { Owner = owner; Projectile = projectile; }
    }
    public static readonly HookChain<EditProjectileArgs> EditProjectile = new();

    /// <summary>
    /// EV_AllowRocketJumping (server/mutators/events.qh) — fired by <c>W_Devastator_DoRemoteExplode</c>
    /// (devastator.qc:74-76) with slot0 seeded to <c>WEP_CVAR(WEP_DEVASTATOR, remote_jump)</c>; a handler
    /// rewrites it to true to force the Devastator's dedicated rocket-jump self-boost blast on regardless of
    /// the weapon's own <c>remote_jump</c> cvar. The Rocket Flying mutator is the stock subscriber.
    /// </summary>
    public struct AllowRocketJumpingArgs
    {
        public bool Allow;   // MUTATOR_ARGV_0_bool (in/out)
        public AllowRocketJumpingArgs(bool allow) { Allow = allow; }
    }
    public static readonly HookChain<AllowRocketJumpingArgs> AllowRocketJumping = new();

    /// <summary>
    /// Fire <see cref="AllowRocketJumping"/> seeded with the weapon's <c>remote_jump</c> cvar and return the
    /// resolved flag (QC <c>MUTATOR_CALLHOOK(AllowRocketJumping, allow_rocketjump); allow_rocketjump =
    /// M_ARGV(0, bool);</c>). The Devastator's remote-explode path calls this stable entry point.
    /// </summary>
    public static bool FireAllowRocketJumping(bool seed)
    {
        var a = new AllowRocketJumpingArgs(seed);
        AllowRocketJumping.Call(ref a);
        return a.Allow;
    }

    /// <summary>
    /// EV_GrappleHookThink (server/mutators/events.qh) — fired each think of an in-flight/latched grappling
    /// hook chain from QC <c>GrapplingHookThink</c> (server/hook.qc). Slot0 the hook entity (its
    /// <c>.realowner</c> is the firing player; QC's tarzan variant also tracks a hooked-player <c>.aiment</c>).
    /// vampirehook subscribes here to drain health from the hooked player each damagerate. NOTE: the port's
    /// grapple latches onto GEOMETRY (no hooked-player aiment), so the vampirehook drain is a documented
    /// partial until the hooked-player ("tarzan") mechanic exists.
    /// </summary>
    public struct GrappleHookThinkArgs
    {
        public readonly Entity Hook;   // MUTATOR_ARGV_0_entity (the grappling hook entity)
        public GrappleHookThinkArgs(Entity hook) { Hook = hook; }
    }
    public static readonly HookChain<GrappleHookThinkArgs> GrappleHookThink = new();

    // ----------------------------------------------------------------------------------------------
    // Presentation
    // ----------------------------------------------------------------------------------------------

    /// <summary>
    /// EV_SetDefaultAlpha (no QC args) — sets the default player/weapon alpha. Modeled as mutable fields
    /// the cloaked mutator writes. A handler returning true mirrors QC's "I set the alpha" return.
    /// </summary>
    public struct SetDefaultAlphaArgs
    {
        public float PlayerAlpha;   // QC default_player_alpha
        public float WeaponAlpha;   // QC default_weapon_alpha
        public SetDefaultAlphaArgs(float playerAlpha, float weaponAlpha)
        {
            PlayerAlpha = playerAlpha; WeaponAlpha = weaponAlpha;
        }
    }
    public static readonly HookChain<SetDefaultAlphaArgs> SetDefaultAlpha = new();

    /// <summary>
    /// QC global <c>default_player_alpha</c> (server/world.qh): the resolved per-spawn player alpha, seeded by
    /// the most recent <see cref="FireSetDefaultAlpha"/> at worldspawn. Lives in Common so the spawn/death/loadout
    /// code (SpawnSystem, DamageSystem) can read the cloaked/running-guns seed without a cross-assembly reach into
    /// the Server GameWorld. 1 = fully opaque (the default until the world-init driver fires).
    /// </summary>
    public static float DefaultPlayerAlpha { get; private set; } = 1f;

    /// <summary>QC global <c>default_weapon_alpha</c> (server/world.qh): the held/exterior weapon spawn alpha (= player alpha under cloaked).</summary>
    public static float DefaultWeaponAlpha { get; private set; } = 1f;

    /// <summary>
    /// Fire <see cref="SetDefaultAlpha"/> and return the resolved (player, weapon) default alpha (QC
    /// <c>SetDefaultAlpha()</c> at world.qc:105-114). Fires the hook chain for mutators (Cloaked, RunningGuns)
    /// to override; if no mutator returns true, the args retain the no-hook default seeded from
    /// <c>autocvar_g_player_alpha</c> with the 0→1 fallback (QC <c>if (default_player_alpha == 0)
    /// default_player_alpha = 1</c>). Returns the resolved (player, weapon) alpha pair and caches it in
    /// the public static defaults for spawn/death to consume.
    /// </summary>
    public static (float playerAlpha, float weaponAlpha) FireSetDefaultAlpha()
    {
        // QC SetDefaultAlpha() default branch (world.qc:109-112): seed from g_player_alpha with 0→1 fallback
        float playerAlpha = 1f;  // default if no cvar is set
        float weaponAlpha = 1f;

        // Read the cvar if available; if 0, apply the fallback (0→1)
        if (Api.Services is not null)
        {
            float g_player_alpha = Api.Cvars.GetFloat("g_player_alpha");
            if (g_player_alpha != 0f)
                playerAlpha = g_player_alpha;
            // else playerAlpha stays 1 (the 0→1 fallback)
        }
        weaponAlpha = playerAlpha; // QC: default_weapon_alpha = default_player_alpha

        // Run the hook chain: mutators (Cloaked, RunningGuns) can override by returning true
        // and rewriting args.PlayerAlpha / args.WeaponAlpha
        var a = new SetDefaultAlphaArgs(playerAlpha, weaponAlpha);
        SetDefaultAlpha.Call(ref a);

        // Cache the resolved seed in Common so the spawn/death/loadout consumers read the same value the
        // Server GameWorld seeds (QC reads the default_player_alpha/default_weapon_alpha globals directly).
        DefaultPlayerAlpha = a.PlayerAlpha;
        DefaultWeaponAlpha = a.WeaponAlpha;
        return (a.PlayerAlpha, a.WeaponAlpha);
    }

    // ----------------------------------------------------------------------------------------------
    // Vehicles (common/vehicles/sv_vehicles.qc MUTATOR_CALLHOOK) — added by the Wave-A2 orchestrator
    // as the stable interface T37 (vehicle seam) fires; no stock mutator subscribes them yet.
    // ----------------------------------------------------------------------------------------------

    /// <summary>
    /// EV_VehicleInit (sv_vehicles.qc:1283) — fired at the end of <c>vehicle_initialize</c>; a handler
    /// returning <c>true</c> ABORTS init (QC: <c>if (MUTATOR_CALLHOOK(VehicleInit, this)) return false;</c>).
    /// Slot0 the vehicle entity.
    /// </summary>
    public struct VehicleInitArgs
    {
        public readonly Entity Vehicle;   // MUTATOR_ARGV_0_entity
        public VehicleInitArgs(Entity vehicle) { Vehicle = vehicle; }
    }
    public static readonly HookChain<VehicleInitArgs> VehicleInit = new();

    /// <summary>
    /// Fire <see cref="VehicleInit"/> for a just-initialised vehicle; returns true if init should ABORT
    /// (QC <c>if (MUTATOR_CALLHOOK(VehicleInit, this)) return false;</c>). The vehicle-framework owner calls
    /// this stable entry point at the end of vehicle initialise.
    /// </summary>
    public static bool FireVehicleInit(Entity vehicle)
    {
        var a = new VehicleInitArgs(vehicle);
        return VehicleInit.Call(ref a);
    }

    /// <summary>
    /// EV_VehicleEnter (sv_vehicles.qc:1072) — a player boarded a vehicle. Slot0 player, slot1 vehicle.
    /// Notify-style (the QC return is unused at this call site).
    /// </summary>
    public struct VehicleEnterArgs
    {
        public readonly Entity Player;    // MUTATOR_ARGV_0_entity
        public readonly Entity Vehicle;   // MUTATOR_ARGV_1_entity
        public VehicleEnterArgs(Entity player, Entity vehicle) { Player = player; Vehicle = vehicle; }
    }
    public static readonly HookChain<VehicleEnterArgs> VehicleEnter = new();

    /// <summary>
    /// EV_VehicleExit (sv_vehicles.qc:848) — a player left a vehicle. Slot0 player (may be null on a
    /// death-eject), slot1 vehicle.
    /// </summary>
    public struct VehicleExitArgs
    {
        public readonly Entity? Player;   // MUTATOR_ARGV_0_entity
        public readonly Entity Vehicle;   // MUTATOR_ARGV_1_entity
        public VehicleExitArgs(Entity? player, Entity vehicle) { Player = player; Vehicle = vehicle; }
    }
    public static readonly HookChain<VehicleExitArgs> VehicleExit = new();

    /// <summary>
    /// EV_VehicleTouch (sv_vehicles.qc:876) — a vehicle touched an entity; a handler returning <c>true</c>
    /// SUPPRESSES the touch (QC: <c>if (MUTATOR_CALLHOOK(VehicleTouch, this, toucher)) return;</c>).
    /// Slot0 vehicle, slot1 toucher.
    /// </summary>
    public struct VehicleTouchArgs
    {
        public readonly Entity Vehicle;   // MUTATOR_ARGV_0_entity
        public readonly Entity Toucher;   // MUTATOR_ARGV_1_entity
        public VehicleTouchArgs(Entity vehicle, Entity toucher) { Vehicle = vehicle; Toucher = toucher; }
    }
    public static readonly HookChain<VehicleTouchArgs> VehicleTouch = new();

    /// <summary>
    /// Fire <see cref="VehicleTouch"/> for a vehicle touching an entity; returns true if the touch should be
    /// SUPPRESSED (QC <c>if (MUTATOR_CALLHOOK(VehicleTouch, this, toucher)) return;</c> — stops the toucher
    /// entering the vehicle). The vehicle-framework owner calls this stable entry point from the touch handler.
    /// </summary>
    public static bool FireVehicleTouch(Entity vehicle, Entity toucher)
    {
        var a = new VehicleTouchArgs(vehicle, toucher);
        return VehicleTouch.Call(ref a);
    }

    // ----------------------------------------------------------------------------------------------
    // PlayerDamaged (server/mutators/events.qh:478 EV_PlayerDamaged) — added by the Wave-A2 orchestrator
    // as the stable interface for damagetext (T51). Fired from DamageSystem.PlayerDamage AFTER the
    // health/armor subtract, carrying the ACTUAL amounts removed (dh/da) + the pre-split potential damage.
    // ----------------------------------------------------------------------------------------------

    /// <summary>
    /// EV_PlayerDamaged — fired on every damage tick once health/armor have been applied. Slot0 attacker
    /// (may be null), slot1 target, slot2 <see cref="Health"/> = health actually removed (QC <c>dh =
    /// initial_health - max(GetResource(this,HEALTH),0)</c>), slot3 <see cref="Armor"/> = armor actually
    /// removed (<c>da</c>), slot4 hit location, slot5 deathtype, slot6 <see cref="PotentialDamage"/> = the
    /// pre-split potential damage. QC returns "forbid logging" (a stats concern the port ignores). damagetext
    /// reads these to build the floating number.
    /// </summary>
    public struct PlayerDamagedArgs
    {
        public readonly Entity? Attacker;       // MUTATOR_ARGV_0_entity
        public readonly Entity Target;          // MUTATOR_ARGV_1_entity
        public readonly float Health;           // MUTATOR_ARGV_2_float (dh — health removed)
        public readonly float Armor;            // MUTATOR_ARGV_3_float (da — armor removed)
        public readonly Vector3 HitLocation;    // MUTATOR_ARGV_4_vector
        public readonly string DeathType;       // MUTATOR_ARGV_5_int (deathtype tag — string in this port)
        public readonly float PotentialDamage;  // MUTATOR_ARGV_6_float

        public PlayerDamagedArgs(Entity? attacker, Entity target, float health, float armor,
            Vector3 hitLocation, string deathType, float potentialDamage)
        {
            Attacker = attacker;
            Target = target;
            Health = health;
            Armor = armor;
            HitLocation = hitLocation;
            DeathType = deathType;
            PotentialDamage = potentialDamage;
        }
    }
    public static readonly HookChain<PlayerDamagedArgs> PlayerDamaged = new();

    // ----------------------------------------------------------------------------------------------
    // Sandbox veto hooks (server/mutators/events.qh:1285-1298) — let another mutator gate the sandbox
    // build mode. Each is a "return true to forbid" hook (Base MUTATOR_CALLHOOK in sv_sandbox.qc).
    // No stock mutator subscribes them; they exist so the sandbox call sites mirror Base exactly
    // (readonly OR hook) rather than gating on g_sandbox_readonly alone.
    // ----------------------------------------------------------------------------------------------

    /// <summary>
    /// EV_Sandbox_DragAllowed (events.qh:1285) — "Return true to prevent sandbox objects from being
    /// dragged". Fired from QC <c>sandbox_ObjectFunction_Think</c> (sv_sandbox.qc:70): a handler returning
    /// <c>true</c> forces the object's grab class to 0 (un-grabbable), exactly like read-only mode. Slot0
    /// the sandbox object entity (MUTATOR_ARGV_0_entity).
    /// </summary>
    public struct SandboxDragAllowedArgs
    {
        public readonly Entity? Object;   // MUTATOR_ARGV_0_entity (the sandbox object)
        public SandboxDragAllowedArgs(Entity? obj) { Object = obj; }
    }
    public static readonly HookChain<SandboxDragAllowedArgs> SandboxDragAllowed = new();

    /// <summary>
    /// Fire <see cref="SandboxDragAllowed"/> for a sandbox object (QC <c>MUTATOR_CALLHOOK(Sandbox_DragAllowed,
    /// this)</c>); returns true if any handler forbids dragging it. The sandbox think tick calls this stable
    /// entry point. The object edict may be null (headless), so slot0 is nullable.
    /// </summary>
    public static bool FireSandboxDragAllowed(Entity? obj)
    {
        var a = new SandboxDragAllowedArgs(obj);
        return SandboxDragAllowed.Call(ref a);
    }

    /// <summary>
    /// EV_Sandbox_SaveAllowed (events.qh:1292, EV_NO_ARGS) — "Return true to prevent writing sandbox changes
    /// to storage". Fired from QC <c>sandbox_Database_Save</c> (sv_sandbox.qc:391): a handler returning
    /// <c>true</c> aborts the save (the per-map storage file is not written). No QC args.
    /// </summary>
    public struct SandboxSaveAllowedArgs
    {
    }
    public static readonly HookChain<SandboxSaveAllowedArgs> SandboxSaveAllowed = new();

    /// <summary>
    /// Fire <see cref="SandboxSaveAllowed"/> (QC <c>MUTATOR_CALLHOOK(Sandbox_SaveAllowed)</c>); returns true
    /// if any handler forbids writing the storage file. The sandbox database-save path calls this stable entry.
    /// </summary>
    public static bool FireSandboxSaveAllowed()
    {
        var a = new SandboxSaveAllowedArgs();
        return SandboxSaveAllowed.Call(ref a);
    }

    /// <summary>
    /// EV_Sandbox_EditAllowed (events.qh:1295) — "Return true to prevent the player from editing sandbox
    /// objects with commands". Fired from QC <c>SV_ParseClientCommand</c> head (sv_sandbox.qc:463): a handler
    /// returning <c>true</c> rejects the player's sandbox command exactly like read-only mode. Slot0 the player
    /// issuing the command (MUTATOR_ARGV_0_entity).
    /// </summary>
    public struct SandboxEditAllowedArgs
    {
        public readonly Entity? Player;   // MUTATOR_ARGV_0_entity
        public SandboxEditAllowedArgs(Entity? player) { Player = player; }
    }
    public static readonly HookChain<SandboxEditAllowedArgs> SandboxEditAllowed = new();

    /// <summary>
    /// Fire <see cref="SandboxEditAllowed"/> for the commanding player (QC <c>MUTATOR_CALLHOOK(Sandbox_EditAllowed,
    /// player)</c>); returns true if any handler forbids the player editing objects. The sandbox command dispatcher
    /// calls this stable entry point at the read-only gate.
    /// </summary>
    public static bool FireSandboxEditAllowed(Entity? player)
    {
        var a = new SandboxEditAllowedArgs(player);
        return SandboxEditAllowed.Call(ref a);
    }

    // ----------------------------------------------------------------------------------------------
    // Item_Spawn (server/mutators/events.qh:631 EV_Item_Spawn) — fired at the TAIL of StartItem
    // for every world item (permanent AND loot) that survived FilterItem. The physical_items mutator
    // subscribes here to spawn the rigid-body ghost entity and hide the real item.
    // ----------------------------------------------------------------------------------------------

    /// <summary>
    /// EV_Item_Spawn (server/mutators/events.qh:631) — fired from <c>StartItem</c> after the item has been
    /// fully initialised (model set, bbox set, touch wired, Item_Reset called). A handler returning
    /// <c>true</c> is treated as "processed" (QC: <c>if (MUTATOR_CALLHOOK(Item_Spawn, this)) return;</c> at
    /// server/items/items.qc:1231 — the mutation is applied). Slot0 the spawning world item entity.
    /// The physical_items mutator is the only stock subscriber; it spawns a physics ghost and hides the real item.
    /// </summary>
    public struct ItemSpawnArgs
    {
        public readonly Entity Item;   // MUTATOR_ARGV_0_entity
        public ItemSpawnArgs(Entity item) { Item = item; }
    }
    public static readonly HookChain<ItemSpawnArgs> ItemSpawn = new();

    /// <summary>
    /// Fire <see cref="ItemSpawn"/> for a just-initialised world item (QC <c>MUTATOR_CALLHOOK(Item_Spawn,
    /// this)</c> at the tail of <c>StartItem</c>). Returns true if a handler "consumed" the spawn (the real
    /// item has been hidden and a ghost spawned). The world-item driver (<see cref="StartItem"/>) calls this
    /// stable entry point after all item setup is complete (past FilterItem, model, bbox, touch, Item_Reset).
    /// </summary>
    public static bool FireItemSpawn(Entity item)
    {
        var a = new ItemSpawnArgs(item);
        return ItemSpawn.Call(ref a);
    }

    // ---- turret hooks (server/mutators/events.qh) -------------------------------------------------------

    /// <summary>
    /// EV_TurretThink (server/mutators/events.qh) — fired at the TOP of QC <c>turret_think</c>
    /// (common/turrets/sv_turrets.qc, step 1: <c>MUTATOR_CALLHOOK(TurretThink, this)</c>) every frame, before
    /// ammo regen / acquire / aim / fire. A handler returning <c>true</c> short-circuits the rest of the brain
    /// for that think (the turret skips its acquire/aim/fire pass this frame). Slot0 the turret. No stock
    /// Xonotic mutator registers this hook; it is wired for parity (exactly like
    /// <see cref="FusionReactorValidTarget"/>) so the <see cref="TurretAI.RunCombat"/> call site matches Base and
    /// a future mod can pause/override a turret's per-frame logic.
    /// </summary>
    public struct TurretThinkArgs
    {
        public readonly Entity Turret;   // MUTATOR_ARGV_0_entity
        public TurretThinkArgs(Entity turret) { Turret = turret; }
    }
    public static readonly HookChain<TurretThinkArgs> TurretThink = new();

    /// <summary>
    /// Fire <see cref="TurretThink"/> at the top of a turret's per-frame brain (QC
    /// <c>MUTATOR_CALLHOOK(TurretThink, this)</c>). Returns true if a handler wants the rest of the think skipped.
    /// The fast path (no handlers) avoids building the args struct on the per-frame turret path.
    /// </summary>
    public static bool FireTurretThink(Entity turret)
    {
        if (TurretThink.Count == 0) return false;   // fast path — no handlers registered
        var a = new TurretThinkArgs(turret);
        return TurretThink.Call(ref a);
    }

    /// <summary>
    /// EV_TurretValidateTarget (server/mutators/events.qh) — fired inside QC <c>turret_validate_target</c>
    /// (common/turrets/sv_turrets.qc), in the reject-cascade right after the alpha-cloak cull and before the
    /// NO/NOTARGET/dead checks: <c>if (MUTATOR_CALLHOOK(TurretValidateTarget, e_turret, e_target, validate_flags))
    /// return M_ARGV(3, float);</c>. A handler that sets <see cref="Override"/> (and returns <c>true</c>) FULLY
    /// REPLACES the validity result — a positive override forces the target valid, a non-positive one rejects it.
    /// The port models the QC float return as a nullable bool: <c>null</c> = fall through (normal cascade decides),
    /// <c>true</c> = force valid, <c>false</c> = force reject. Slot0 the turret, slot1 the candidate target, slot2
    /// the select flags. No stock Xonotic mutator registers this hook; it is wired for parity so
    /// <see cref="TurretAI.ValidTarget"/> matches Base.
    /// </summary>
    public struct TurretValidateTargetArgs
    {
        public readonly Entity Turret;       // MUTATOR_ARGV_0_entity
        public readonly Entity Target;        // MUTATOR_ARGV_1_entity
        public readonly int ValidateFlags;    // MUTATOR_ARGV_2_float (target_select_flags)
        /// <summary><c>null</c> = fall through; <c>true</c> = force valid; <c>false</c> = force reject
        /// (in/out — set by handlers, read by the Fire helper).</summary>
        public bool? Override;                // MUTATOR_ARGV_3 (in/out)
        public TurretValidateTargetArgs(Entity turret, Entity target, int validateFlags)
        { Turret = turret; Target = target; ValidateFlags = validateFlags; Override = null; }
    }
    public static readonly HookChain<TurretValidateTargetArgs> TurretValidateTarget = new();

    /// <summary>
    /// Fire <see cref="TurretValidateTarget"/> for a candidate target (QC
    /// <c>MUTATOR_CALLHOOK(TurretValidateTarget, e_turret, e_target, validate_flags)</c>) and return the resolved
    /// override: <c>true</c> force valid, <c>false</c> force reject, <c>null</c> fall through to the normal cascade.
    /// </summary>
    public static bool? FireTurretValidateTarget(Entity turret, Entity target, int validateFlags)
    {
        if (TurretValidateTarget.Count == 0) return null;   // fast path — no handlers registered
        var a = new TurretValidateTargetArgs(turret, target, validateFlags);
        TurretValidateTarget.Call(ref a);
        return a.Override;
    }

    /// <summary>
    /// EV_Turret_CheckFire (server/mutators/events.qh) — fired near the top of QC <c>turret_firecheck</c>
    /// (common/turrets/sv_turrets.qc): <c>if (MUTATOR_CALLHOOK(Turret_CheckFire, this)) return M_ARGV(1,
    /// float);</c>. A handler can FORCE the firecheck result: <see cref="Override"/> = true forces the turret to
    /// fire this think (skipping the refire/ammo/distance/AFF/AIMDIST gates), false forces it to hold. The port
    /// models the QC float return as a nullable bool (<c>null</c> = fall through). Slot0 the turret. No stock
    /// Xonotic mutator registers this hook; wired for parity.
    /// </summary>
    public struct TurretCheckFireArgs
    {
        public readonly Entity Turret;   // MUTATOR_ARGV_0_entity
        /// <summary><c>null</c> = fall through; <c>true</c> = force fire; <c>false</c> = force hold (in/out).</summary>
        public bool? Override;           // MUTATOR_ARGV_1 (in/out)
        public TurretCheckFireArgs(Entity turret) { Turret = turret; Override = null; }
    }
    public static readonly HookChain<TurretCheckFireArgs> TurretCheckFire = new();

    /// <summary>
    /// Fire <see cref="TurretCheckFire"/> at the top of the fire gate (QC
    /// <c>MUTATOR_CALLHOOK(Turret_CheckFire, this)</c>). Returns the forced result: <c>true</c> force fire,
    /// <c>false</c> force hold, <c>null</c> fall through to the normal firecheck gates.
    /// </summary>
    public static bool? FireTurretCheckFire(Entity turret)
    {
        if (TurretCheckFire.Count == 0) return null;   // fast path — no handlers registered
        var a = new TurretCheckFireArgs(turret);
        TurretCheckFire.Call(ref a);
        return a.Override;
    }

    /// <summary>
    /// EV_TurretFire (server/mutators/events.qh) — fired from QC <c>turret_fire</c>
    /// (common/turrets/sv_turrets.qc) just before the turret runs its weapon: <c>if
    /// (MUTATOR_CALLHOOK(TurretFire, this)) return;</c>. A handler returning <c>true</c> SUPPRESSES the shot
    /// (no weapon, no refire/ammo/volley bookkeeping) for that fire — the per-mutator twin of the
    /// <c>g_turrets_nofire</c> master gate. Slot0 the turret. No stock Xonotic mutator registers this hook;
    /// wired for parity so the <see cref="TurretAI.Fire"/> call site matches Base.
    /// </summary>
    public struct TurretFireArgs
    {
        public readonly Entity Turret;   // MUTATOR_ARGV_0_entity
        public TurretFireArgs(Entity turret) { Turret = turret; }
    }
    public static readonly HookChain<TurretFireArgs> TurretFire = new();

    /// <summary>
    /// Fire <see cref="TurretFire"/> just before a turret shoots (QC <c>MUTATOR_CALLHOOK(TurretFire, this)</c>).
    /// Returns true if a handler wants the shot suppressed. Fast path skips the args struct when no handler is set.
    /// </summary>
    public static bool FireTurretFire(Entity turret)
    {
        if (TurretFire.Count == 0) return false;   // fast path — no handlers registered
        var a = new TurretFireArgs(turret);
        return TurretFire.Call(ref a);
    }

    /// <summary>
    /// EV_FusionReactor_ValidTarget (server/mutators/events.qh:1194-1203) — fired from the Fusion Reactor
    /// firecheck (<c>turret_fusionreactor_firecheck</c>, fusionreactor.qc:9-13) for each candidate recharge
    /// target. A handler may force-accept or force-reject the target; if no handler fires (or all fall through)
    /// the normal firecheck gates decide.
    ///
    /// The QC return is a three-valued enum:
    ///   <c>MUT_FUSREAC_TARG_CONTINUE</c> (0) — fall through; the normal firecheck continues.
    ///   <c>MUT_FUSREAC_TARG_VALID</c>    (1) — force accept (return true from firecheck).
    ///   <c>MUT_FUSREAC_TARG_INVALID</c>  (2) — force reject (return false from firecheck).
    ///
    /// The port models this with a nullable <see cref="Override"/>: <c>null</c> = continue (no handler set
    /// it), <c>true</c> = VALID, <c>false</c> = INVALID. Handlers that want CONTINUE simply leave
    /// <see cref="Override"/> at its null default and return <c>false</c>. Handlers that want VALID set
    /// <see cref="Override"/> = true and return <c>true</c>; INVALID handlers set it false and return true.
    /// <see cref="FireFusionReactorValidTarget"/> reads the field after the chain runs.
    ///
    /// No stock Xonotic mutator registers this hook; it is present so the call site in
    /// <see cref="FusionReactorTurret"/> matches Base exactly and a future mod can intercept recharging.
    /// </summary>
    public struct FusionReactorValidTargetArgs
    {
        public readonly Entity Turret;   // MUTATOR_ARGV_0_entity — the reactor
        public readonly Entity Target;   // MUTATOR_ARGV_1_entity — the candidate recipient
        /// <summary>
        /// <c>null</c> = fall through (MUT_FUSREAC_TARG_CONTINUE); <c>true</c> = force accept
        /// (MUT_FUSREAC_TARG_VALID); <c>false</c> = force reject (MUT_FUSREAC_TARG_INVALID).
        /// </summary>
        public bool? Override;           // in/out — set by handlers, read by the Fire helper
        public FusionReactorValidTargetArgs(Entity turret, Entity target)
        { Turret = turret; Target = target; Override = null; }
    }
    public static readonly HookChain<FusionReactorValidTargetArgs> FusionReactorValidTarget = new();

    /// <summary>
    /// Fire <see cref="FusionReactorValidTarget"/> for a candidate recharge target (QC
    /// <c>MUTATOR_CALLHOOK(FusionReactor_ValidTarget, this, targ)</c>) and return the resolved tri-state:
    /// <c>true</c> if a handler forced VALID, <c>false</c> if one forced INVALID, <c>null</c> if all fell
    /// through (normal firecheck gates apply). The Fusion Reactor's firecheck calls this and branches on the
    /// result before applying its own gates.
    /// </summary>
    public static bool? FireFusionReactorValidTarget(Entity turret, Entity target)
    {
        if (FusionReactorValidTarget.Count == 0) return null;   // fast path — no handlers registered
        var a = new FusionReactorValidTargetArgs(turret, target);
        FusionReactorValidTarget.Call(ref a);
        return a.Override;   // null = continue; true = force valid; false = force invalid
    }

    // ---- match-end hooks (server/world.qc NextLevel) ----------------------------------------------------

    /// <summary>
    /// EV_MatchEnd_BeforeScores (server/mutators/events.qh) — fired near the TOP of <c>NextLevel()</c>, before
    /// the per-player scores/stats are dumped (right after <c>VoteReset</c>). Lets a mode lock in final state
    /// that the scoreboard then reports (e.g. ClanArena/Survival end-of-match score adjustments). No args.
    /// </summary>
    public struct MatchEndBeforeScoresArgs { }
    public static readonly HookChain<MatchEndBeforeScoresArgs> MatchEndBeforeScores = new();

    /// <summary>Fire <see cref="MatchEndBeforeScores"/> (QC <c>MUTATOR_CALLHOOK(MatchEnd_BeforeScores)</c>).</summary>
    public static bool FireMatchEndBeforeScores()
    {
        var a = new MatchEndBeforeScoresArgs();
        return MatchEndBeforeScores.Call(ref a);
    }

    /// <summary>
    /// EV_MatchEnd (server/mutators/events.qh) — fired near the END of <c>NextLevel()</c>, after the winner
    /// banner + intermission setup, just before <c>localcmd("sv_hook_gameend")</c>. The CTF flag cleanup,
    /// KeyHunt <c>kh_finalize()</c>, and the instagib countdown stop hang off this. No args.
    /// </summary>
    public struct MatchEndArgs { }
    public static readonly HookChain<MatchEndArgs> MatchEnd = new();

    /// <summary>Fire <see cref="MatchEnd"/> (QC <c>MUTATOR_CALLHOOK(MatchEnd)</c>).</summary>
    public static bool FireMatchEnd()
    {
        var a = new MatchEndArgs();
        return MatchEnd.Call(ref a);
    }

    // Nade mutator hooks (common/mutators/mutator/nades/sv_nades.qc MUTATOR_HOOKABLE events)

    /// <summary>
    /// EV_Nade_Damage — fired from NadeProjectile.NadeDamage for each nade hit; a handler returning true
    /// consumes the hook (the nade's damage is adjusted per the mutator's logic, and other handlers don't
    /// run). Slot0 the nade entity, slot1 the attacking weapon (deathtype-resolved), slot2 damage (in/out),
    /// slot3 force (in/out). okhmg_nadesupport (okhmg.qc:5-12) subscribes here to scale nade self-damage
    /// when the nade is damaged by the HMG.
    /// </summary>
    public struct NadeDamageArgs
    {
        public readonly Entity Nade;         // MUTATOR_ARGV_0_entity
        public readonly string Weapon;       // MUTATOR_ARGV_1_string (weapon NetName from deathtype)
        public float Damage;                 // MUTATOR_ARGV_2_float (in/out)
        public Vector3 Force;                // MUTATOR_ARGV_3_vector (in/out)

        public NadeDamageArgs(Entity nade, string weapon, float damage, Vector3 force)
        {
            Nade = nade;
            Weapon = weapon;
            Damage = damage;
            Force = force;
        }
    }
    public static readonly HookChain<NadeDamageArgs> NadeDamage = new();

    /// <summary>
    /// Fire <see cref="NadeDamage"/> for a nade being damaged (QC <c>MUTATOR_CALLHOOK(Nade_Damage,
    /// this, attacking_weapon, damage, force)</c>). Returns true if a handler consumed the hook (damage
    /// scaling). The nade damage path (<see cref="NadeProjectile.NadeDamage"/>) calls this to allow
    /// per-weapon damage adjustments.
    /// </summary>
    public static bool FireNadeDamage(ref NadeDamageArgs args)
    {
        return NadeDamage.Call(ref args);
    }

    // ---- Client-side (HUD / presentation) ----

    /// <summary>
    /// EV_AnnouncerOption (client/mutators/events.qh) — "announcer string" hook that lets a mutator override the
    /// announcer voice pack. Slot0 is the announcer string name (in/out); a handler may rewrite it to a different
    /// voice-pack directory name. QC: <c>MUTATOR_CALLHOOK(AnnouncerOption, ret)</c> (client/announcer.qc:14-20),
    /// then <c>ret = M_ARGV(0, string)</c> is consumed by <c>AnnouncerFilename()</c> to resolve the sound path.
    /// Fired from NetGame when the HUD sound state is set up.
    ///
    /// NOTE: the hookable exists for parity, but NO stock Base mutator registers a handler for it (verified across
    /// qcsrc — including overkill, which has zero announcer references and ships no "overkill" voice pack; Base
    /// ships only the "default" pack). So out of the box this hook is a no-op pass-through, exactly like Base. Do
    /// not add a handler that swaps to a voice pack that doesn't exist — that would silence the announcer.
    /// </summary>
    public struct AnnouncerOptionArgs
    {
        public string Voice;   // MUTATOR_ARGV_0_string (in/out)
        public AnnouncerOptionArgs(string voice) { Voice = voice; }
    }
    public static readonly HookChain<AnnouncerOptionArgs> AnnouncerOption = new();

    /// <summary>
    /// Fire <see cref="AnnouncerOption"/> to let mutators override the announcer voice pack (QC
    /// <c>MUTATOR_CALLHOOK(AnnouncerOption, voice); voice = M_ARGV(0, string);</c>). Returns the resolved voice
    /// name. The HUD initialization path calls this after reading <c>cl_announcer</c>; with no handler registered
    /// (the stock case) it returns <paramref name="voice"/> unchanged — matching Base's no-op pass-through.
    /// </summary>
    public static string FireAnnouncerOption(string voice)
    {
        var a = new AnnouncerOptionArgs(voice);
        AnnouncerOption.Call(ref a);
        return a.Voice;
    }
}

/// <summary>
/// The mutable start-of-life loadout — the C# stand-in for QuakeC's start_health / start_armorvalue /
/// start_weapons / start_ammo_* globals (and their warmup_* twins) that SetStartItems hooks mutate
/// (server/world.qc, common/items). This is the spawn-config layer: weapons are tracked as a set of
/// weapon NetNames (readable, registry-order-independent); the arena mutators additionally apply the
/// concrete owned-weapon <see cref="WepSet"/> per spawn through <see cref="Inventory"/> in their PlayerSpawn
/// hooks. Warmup mirrors the live values (StartLoadout has no separate warmup_* twins).
/// </summary>
public sealed class StartLoadout
{
    public float Health = 100f;
    public float Armor = 0f;

    public float AmmoShells;
    public float AmmoBullets;   // QC start_ammo_nails
    public float AmmoRockets;
    public float AmmoCells;
    public float AmmoFuel;

    /// <summary>
    /// QC <c>warmup_start_ammo_fuel</c> (world.qc:2127/2140/2167) — the warmup twin of <see cref="AmmoFuel"/>.
    /// Mirrors the live fuel by default; <c>SetStartItems</c> handlers that bump fuel (the hook mutator's
    /// fuel-regen grant) max this independently so warmup spawns get the same fuel. Consumed by the warmup
    /// loadout path.
    /// </summary>
    public float WarmupAmmoFuel;

    /// <summary>Weapon NetNames granted at spawn (QC start_weapons bitset). e.g. "vaporizer", "shotgun".</summary>
    public readonly HashSet<string> Weapons = new(StringComparer.Ordinal);

    /// <summary>QC start_items bitmask flags (IT_UNLIMITED_AMMO etc.) collected as string tags for now.</summary>
    public readonly HashSet<string> ItemFlags = new(StringComparer.Ordinal);

    public void SetWeapons(params string[] netNames)
    {
        Weapons.Clear();
        foreach (string n in netNames) Weapons.Add(n);
    }
}
