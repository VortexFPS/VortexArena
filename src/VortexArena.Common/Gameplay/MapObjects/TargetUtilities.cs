// Port of the target_* utility entities + func_door_secret from qcsrc/common/mapobjects/:
//   target/kill.qc       -> target_kill        (damage the activator 1000, DEATH_HURTTRIGGER/void)
//   target/speed.qc      -> target_speed       (recompute the activator's velocity from speed + axis mask)
//   target/spawnpoint.qc -> target_spawnpoint  (force the activator's next spawn point)
//   target/location.qc   -> target_location / info_location (HUD location-name volumes)
//   target/changelevel.qc-> target_changelevel (end / switch level; multiplayer fraction)
//   target/levelwarp.qc  -> target_levelwarp   (campaign level warp)
//   target/give.qc       -> target_give        (give the activator the items pointed to)
//   target/items.qc      -> target_items       (set/add items+resources+powerups via a token string)
//   target/spawn.qc      -> target_spawn       (MINIMAL: ON_MAPLOAD spawn only; the $-templating DB is cut)
//   func/door_secret.qc  -> func_door_secret   (slide-back-then-side secret door)
//
// These are .use-activated (no volume) except door_secret (a moving brush). They fire SUB_UseTargets via
// MapMover.UseTargets. As elsewhere, QC's 3-arg .use(this, actor, trigger) maps to the port's 2-arg
// EntityUse(self, activator); the dropped `trigger` is only forwarded as the (unused) downstream trigger.
//
// CROSS-LAYER NOTE: target_changelevel / target_levelwarp need server/campaign behavior that lives ABOVE
// XonoticGodot.Common (XonoticGodot.Server: GameWorld.NextLevel, Commands.ChangeLevelHandler, Campaign.LevelWarp).
// XonoticGodot.Common cannot reference those, so this file exposes static seam delegates (mirroring
// WarpzoneSpawns.Sink / GametypeObjectiveSpawns.Sink) the host wires up; unwired they degrade to a no-op.

using System.Numerics;
using XonoticGodot.Common.Diagnostics;
using XonoticGodot.Common.Framework;
using XonoticGodot.Common.Gameplay.Damage;
using XonoticGodot.Common.Gameplay.Scoring;
using XonoticGodot.Common.Math;
using XonoticGodot.Common.Services;

namespace XonoticGodot.Common.Gameplay;

/// <summary>
/// The target_* utility entities (kill / speed / spawnpoint / location / changelevel / levelwarp / give /
/// items / spawn) plus func_door_secret. Each setup is a spawnfunc registered by <see cref="MapObjectsRegistry"/>.
/// </summary>
public static class TargetUtilities
{
    // ===================================================================
    //  target_kill (target/kill.qc) — damage the activator to death
    // ===================================================================

    /// <summary>
    /// <c>spawnfunc(target_kill)</c> — when used, deals 1000 void damage to the activator (mirrors
    /// trigger_hurt's bite). Only affects damageable creatures/projectiles.
    /// </summary>
    public static void KillSetup(Entity this_)
    {
        if (string.IsNullOrEmpty(this_.Message))
            this_.Message = "was in the wrong place";
        if (string.IsNullOrEmpty(this_.Message2))
            this_.Message2 = "was thrown into a world of hurt by";

        this_.Use = KillUse;
        this_.Active = MapMover.ActiveActive;
        this_.ClassName = "target_kill";
        MapMover.IndexRegister(this_);
        // QC: this.reset = target_kill_reset (active = ACTIVE_ACTIVE) — dormant until reset infra exists.
    }

    /// <summary>QC <c>target_kill_use</c>: 1000 void damage to a damageable creature/projectile activator.</summary>
    private static void KillUse(Entity self, Entity actor)
    {
        if (self.Active != MapMover.ActiveActive)
            return;
        if (actor.TakeDamage == DamageMode.No)
            return;
        // QC: if(!actor.iscreature && !actor.damagedbytriggers) return; — the port has no damagedbytriggers
        // field; IsPushable covers projectiles/loot/bodies (what damagedbytriggers tags), matching the intent.
        if (!MapMover.IsCreature(actor) && !MapMover.IsPushable(actor))
            return;

        // QC DEATH_HURTTRIGGER (used as the void/hurt-trigger death). trigger is dropped at the .use boundary.
        Combat.Damage(actor, self, null, 1000f, DeathTypes.Void, actor.Origin, Vector3.Zero);
    }

    // ===================================================================
    //  target_speed (target/speed.qc) — recompute the activator's velocity
    // ===================================================================

    public const int SpeedPercentage = 1 << 0; // SPEED_PERCENTAGE
    public const int SpeedAdd = 1 << 1;         // SPEED_ADD
    public const int SpeedPositiveX = 1 << 2;   // SPEED_POSITIVE_X
    public const int SpeedNegativeX = 1 << 3;   // SPEED_NEGATIVE_X
    public const int SpeedPositiveY = 1 << 4;   // SPEED_POSITIVE_Y
    public const int SpeedNegativeY = 1 << 5;   // SPEED_NEGATIVE_Y
    public const int SpeedPositiveZ = 1 << 6;   // SPEED_POSITIVE_Z
    public const int SpeedNegativeZ = 1 << 7;   // SPEED_NEGATIVE_Z
    public const int SpeedLauncher = 1 << 8;    // SPEED_LAUNCHER

    /// <summary>
    /// <c>spawnfunc(target_speed)</c> — when used, overwrites the activator's velocity per the speed value and
    /// the per-axis spawnflag mask (percentage / add / launcher). Default speed 100. (The CSQC networking +
    /// generic_netlinked_setactive of the original are skipped; this is the server behavior.)
    /// </summary>
    public static void SpeedSetup(Entity this_)
    {
        this_.Active = MapMover.ActiveActive;
        this_.Use = SpeedUse;
        this_.ClassName = "target_speed";

        // QC: support a 0 speed setting AND a default — only default when the key was absent. The port's
        // spawn loader leaves Speed at 0 for an absent key; a present "0" is indistinguishable here, so match
        // the common case (default 100 when 0). A map that genuinely wants 0 can use the percentage flag.
        if (this_.Speed == 0f)
            this_.Speed = 100f;

        MapMover.IndexRegister(this_);
        // QC: this.reset = target_speed_reset (active = ACTIVE_ACTIVE) — dormant until reset infra exists.
    }

    /// <summary>QC <c>target_speed_use</c>.</summary>
    private static void SpeedUse(Entity self, Entity actor)
    {
        if (self.Active != MapMover.ActiveActive)
            return;
        actor.Velocity = CalculateVelocity(self, self.Speed, actor);
    }

    /// <summary>
    /// Port of <c>target_speed_calculatevelocity</c> (speed.qc): build the new velocity from
    /// <paramref name="speed"/>, the per-axis positive/negative spawnflag mask, and the percentage/add/launcher
    /// modes. The Q3-compat launcher bug branch (STAT(Q3COMPAT)) is NOT taken — the port has no Q3COMPAT flag,
    /// so the faithful non-Q3 path is used (launcherspeed accumulated once, outside the loop).
    /// </summary>
    public static Vector3 CalculateVelocity(Entity self, float speed, Entity pushedEntity)
    {
        bool isPercentage = (self.SpawnFlags & SpeedPercentage) != 0;
        bool isAdd = (self.SpawnFlags & SpeedAdd) != 0;
        bool isLauncher = (self.SpawnFlags & SpeedLauncher) != 0;

        var isPositive = new bool[3];
        isPositive[0] = (self.SpawnFlags & SpeedPositiveX) != 0;
        isPositive[1] = (self.SpawnFlags & SpeedPositiveY) != 0;
        isPositive[2] = (self.SpawnFlags & SpeedPositiveZ) != 0;

        var isNegative = new bool[3];
        isNegative[0] = (self.SpawnFlags & SpeedNegativeX) != 0;
        isNegative[1] = (self.SpawnFlags & SpeedNegativeY) != 0;
        isNegative[2] = (self.SpawnFlags & SpeedNegativeZ) != 0;

        // speed cannot be negative except when subtracting (add mode)
        if (!isAdd)
            speed = MathF.Max(speed, 0f);

        var pushvel = ToArray(pushedEntity.Velocity);

        for (int i = 0; i < 3; ++i)
        {
            // launcher can only be either positive or negative, not both
            if (isLauncher && isPositive[i] && isNegative[i])
                isPositive[i] = isNegative[i] = false;

            // ignore this direction
            if (!isPositive[i] && !isNegative[i])
                pushvel[i] = 0f;
        }

        float oldspeed = FromArray(pushvel).Length();

        // the speed field is used as a percentage of the current speed
        if (isPercentage)
            speed = oldspeed * speed / 100f;

        float launcherspeed = 0f;

        // non-Q3 path (the only path here): accumulate launcherspeed once, outside the loop.
        launcherspeed += speed;
        if (isAdd)
            launcherspeed += oldspeed;

        for (int i = 0; i < 3; ++i)
        {
            if ((pushvel[i] != 0f || isLauncher) && (isPositive[i] != isNegative[i]))
            {
                if (isLauncher)
                {
                    // every direction weighs the same on launchers; movedir does not matter.
                    pushvel[i] = 1f;
                    // (the Q3-compat in-loop launcherspeed bug is intentionally skipped)
                }

                if (isPositive[i])
                    pushvel[i] = MathF.CopySign(pushvel[i], 1f);
                else if (isNegative[i])
                    pushvel[i] = MathF.CopySign(pushvel[i], -1f);
            }
        }

        var oldvel = ToArray(pushedEntity.Velocity);

        if (isLauncher)
        {
            // a launcher always launches in the correct direction even for a negative speed (fabs is correct).
            FromArrayInto(pushvel, QMath.Normalize(FromArray(pushvel)) * MathF.Abs(launcherspeed));
        }
        else if (!isAdd)
        {
            FromArrayInto(pushvel, QMath.Normalize(FromArray(pushvel)) * speed);
        }
        else
        {
            FromArrayInto(pushvel, QMath.Normalize(FromArray(pushvel)) * speed + FromArray(oldvel));
        }

        for (int i = 0; i < 3; ++i)
        {
            // preserve unaffected directions
            if (!isPositive[i] && !isNegative[i])
                pushvel[i] = oldvel[i];
        }

        return FromArray(pushvel);
    }

    private static float[] ToArray(Vector3 v) => new[] { v.X, v.Y, v.Z };
    private static Vector3 FromArray(float[] a) => new(a[0], a[1], a[2]);
    private static void FromArrayInto(float[] a, Vector3 v) { a[0] = v.X; a[1] = v.Y; a[2] = v.Z; }

    // ===================================================================
    //  target_spawnpoint (target/spawnpoint.qc) — force the activator's next spawn
    // ===================================================================

    /// <summary><c>spawnfunc(target_spawnpoint)</c> — when used, sets the activator's forced spawn point.</summary>
    public static void SpawnPointSetup(Entity this_)
    {
        this_.Active = MapMover.ActiveActive;
        this_.Use = SpawnPointUse;
        this_.ClassName = "target_spawnpoint";
        MapMover.IndexRegister(this_);
        // QC: this.reset = target_spawnpoint_reset — dormant until reset infra exists.
    }

    /// <summary>QC <c>target_spawnpoint_use</c>: actor.spawnpoint_targ = this (SpawnSystem honors it when selecting).</summary>
    private static void SpawnPointUse(Entity self, Entity actor)
    {
        if (self.Active != MapMover.ActiveActive)
            return;
        actor.SpawnPointTarg = self;
    }

    // ===================================================================
    //  target_location / info_location (target/location.qc) — HUD location names
    // ===================================================================

    /// <summary>
    /// <c>spawnfunc(target_location)</c> — a passive point entity whose <c>.netname</c> labels a region for the
    /// HUD (the "%l" location name). Pushed to <see cref="MapObjectsState.Locations"/>; the HUD picks the
    /// nearest one to a player.
    /// </summary>
    public static void LocationSetup(Entity this_)
    {
        // QC target_push_init: make it a passive point ent (SOLID_NOT, no touch). location name in .netname.
        this_.Solid = Solid.Not;
        this_.Touch = null;
        this_.ClassName = "target_location";
        MapObjectsState.Locations.Add(this_);
        MapMover.IndexRegister(this_);
    }

    /// <summary><c>spawnfunc(info_location)</c> — like target_location but the label comes via <c>.message</c>.</summary>
    public static void InfoLocationSetup(Entity this_)
    {
        this_.Message = this_.NetName;
        LocationSetup(this_);
        this_.ClassName = "info_location";
    }

    // ===================================================================
    //  target_changelevel (target/changelevel.qc) — end / switch the level
    // ===================================================================

    public const int ChangeLevelMultiplayer = 1 << 1; // CHANGELEVEL_MULTIPLAYER = BIT(1) (changelevel.qh)

    /// <summary>
    /// Host seam (XonoticGodot.Server): end the current match / advance to the next level (QC <c>NextLevel()</c>),
    /// optionally flagging a campaign win because a real player triggered it. Null = degrade to a no-op.
    /// </summary>
    public static System.Action<Entity /*actor*/>? NextLevelHandler;

    /// <summary>
    /// Host seam (XonoticGodot.Server): switch to a named map (QC <c>changelevel(chmap)</c>). Null = degrade to a
    /// no-op (a bare unit test, or a host that hasn't wired the command path).
    /// </summary>
    public static System.Action<string /*chmap*/>? ChangeLevelHandler;

    /// <summary>
    /// Host seam (XonoticGodot.Server): the count of REAL (non-bot) clients + how many have voted for a given
    /// changelevel target — the CHANGELEVEL_MULTIPLAYER fraction needs the whole client list, which lives in the
    /// server. Returns (real-player count, count whose ChLevelTarg == target). Null = treat as a solo trigger.
    /// </summary>
    public static System.Func<Entity /*target*/, (int real, int voted)>? RealPlayerVoteCount;

    /// <summary>
    /// Host seam (XonoticGodot.Server): apply the next map's gametype (QC <c>MapInfo_SwitchGameType</c>) when a
    /// <c>target_changelevel</c> carries a <c>.gametype</c> NetName. On the listen server this sets the live
    /// <c>gametype</c> cvar so the rebooted level comes up in that mode. Null = no switch (degrade to no-op).
    /// </summary>
    public static System.Action<string /*gametype NetName*/>? SwitchGameTypeHandler;

    /// <summary>
    /// <c>spawnfunc(target_changelevel)</c> — ends the match (empty <c>.chmap</c>) or switches to <c>.chmap</c>.
    /// CHANGELEVEL_MULTIPLAYER requires a fraction (<c>.count</c>, default 0.7) of real players to trigger it.
    /// </summary>
    public static void ChangeLevelSetup(Entity this_)
    {
        this_.Use = ChangeLevelUse;
        this_.Active = MapMover.ActiveActive;
        this_.ClassName = "target_changelevel";

        // QC: if(!this.count) this.count = 0.7 — a fraction of real players. QC's .count is a FLOAT; the port
        // binds the raw "count" key as a float onto ParticleCount (MapObjectFieldsExtra) AND truncated onto the
        // int Count. Seed the fraction from the FLOAT count so a fractional map value (e.g. 0.5) survives — fall
        // back to the int Count, then the 0.7 default. (Maps virtually always use the default fraction.)
        float countKey = this_.ParticleCount != 0f ? this_.ParticleCount : this_.Count;
        this_.MoverCnt = countKey != 0f ? countKey : 0.7f;

        MapMover.IndexRegister(this_);
        // QC: this.reset = target_changelevel_reset — dormant until reset infra exists.
    }

    /// <summary>QC <c>target_changelevel_use</c>.</summary>
    private static void ChangeLevelUse(Entity self, Entity actor)
    {
        if (GameScores.GameStopped)
            return;
        if (self.Active != MapMover.ActiveActive)
            return;

        if ((self.SpawnFlags & ChangeLevelMultiplayer) != 0)
        {
            // simply don't react if a non-player triggers it
            if ((actor.Flags & EntFlags.Client) == 0)
                return;

            actor.ChLevelTarg = self;

            // count real players + how many voted for THIS changelevel (the host supplies the client list).
            int realplnum, plnum;
            if (RealPlayerVoteCount is not null)
            {
                (realplnum, plnum) = RealPlayerVoteCount(self);
            }
            else
            {
                // No host client list (a bare test): treat the single triggering player as the whole vote.
                realplnum = 1;
                plnum = 1;
            }

            // QC: if(plnum < ceil(realplnum * min(1, this.count))) return;
            if (plnum < (int)MathF.Ceiling(realplnum * MathF.Min(1f, self.MoverCnt)))
                return;
        }

        // QC: if(this.gametype != "") MapInfo_SwitchGameType(MapInfo_Type_FromString(this.gametype)). On the
        // listen server the host seam sets the live `gametype` cvar so the next changelevel boots that mode
        // (the port's MapInfo_SwitchGameType equivalent). Unwired → degrades to no-op.
        if (!string.IsNullOrEmpty(self.ChLevelGameType))
        {
            if (SwitchGameTypeHandler is not null)
                SwitchGameTypeHandler(self.ChLevelGameType);
            else
                Log.Trace($"target_changelevel: gametype switch to '{self.ChLevelGameType}' requested (unwired).");
        }

        if (string.IsNullOrEmpty(self.ChMap))
        {
            // empty chmap -> end the match (QC NextLevel(); campaign win if a real client triggered it).
            NextLevelHandler?.Invoke(actor);
        }
        else
        {
            ChangeLevelHandler?.Invoke(self.ChMap);
        }
    }

    // ===================================================================
    //  target_levelwarp (target/levelwarp.qc) — campaign level warp
    // ===================================================================

    /// <summary>
    /// Host seam (XonoticGodot.Server): campaign level warp (QC <c>CampaignLevelWarp(n)</c>) — n is the 0-based level
    /// index, or -1 for the next level. Null = degrade to a no-op (not in a campaign, or unwired).
    /// </summary>
    public static System.Action<int /*level*/>? CampaignLevelWarpHandler;

    /// <summary>Host seam: QC <c>autocvar_g_campaign</c> — true only during a campaign. Default false (skirmish).</summary>
    public static System.Func<bool>? IsCampaign;

    /// <summary>
    /// <c>spawnfunc(target_levelwarp)</c> — campaign-only: warps to campaign level <c>.cnt</c> (1-based; 0 = next).
    /// </summary>
    public static void LevelWarpSetup(Entity this_)
    {
        this_.Use = LevelWarpUse;
        this_.Active = MapMover.ActiveActive;
        this_.ClassName = "target_levelwarp";
        MapMover.IndexRegister(this_);
        // QC: this.reset = target_levelwarp_reset — dormant until reset infra exists.
    }

    /// <summary>QC <c>target_levelwarp_use</c>: CampaignLevelWarp(cnt-1) for a specific level, else (-1) for next.</summary>
    private static void LevelWarpUse(Entity self, Entity actor)
    {
        if (IsCampaign is null || !IsCampaign())
            return; // only in campaign
        if (self.Active != MapMover.ActiveActive)
            return;

        if (self.Cnt != 0)
            CampaignLevelWarpHandler?.Invoke(self.Cnt - 1); // specific level (cnt is 1-based)
        else
            CampaignLevelWarpHandler?.Invoke(-1);           // next level
    }

    // ===================================================================
    //  target_give (target/give.qc) — give the activator the items pointed to
    // ===================================================================

    /// <summary>
    /// Host seam: apply a world item <c>worldItem</c>'s pickup to <c>actor</c> (QC ITEM_HANDLE(Pickup,…)) and
    /// return whether anything was taken. The Common layer has no world-item -&gt; Pickup link nor a g_items
    /// list, so target_give routes the give through this seam the host provides (mirrors the gametype sinks).
    /// Null = the give degrades to firing the item's own targets only (the SUB_UseTargets tail still runs).
    /// </summary>
    public static System.Func<Entity /*worldItem*/, Entity /*actor*/, bool>? GiveItemHandler;

    /// <summary>
    /// <c>spawnfunc(target_give)</c> — gives the activator every item entity pointed to by <c>.target</c>, then
    /// fires each item's own targets.
    /// </summary>
    public static void GiveSetup(Entity this_)
    {
        this_.Use = GiveUse;
        this_.ClassName = "target_give";
        MapMover.IndexRegister(this_);
    }

    /// <summary>QC <c>target_give_use</c>: for each item named by <c>.target</c>, hand it to the player + relay its targets.</summary>
    private static void GiveUse(Entity self, Entity actor)
    {
        if ((actor.Flags & EntFlags.Client) == 0 || MapMover.IsDead(actor))
            return;

        // QC: IL_EACH(g_items, it.targetname == this.target, …). The port has no g_items IL; resolve item
        // entities by targetname (the index includes every spawned map object, items among them).
        foreach (Entity it in MapMover.FindByTargetName(self.Target).ToList())
        {
            // Only entities that are items (QC's g_items membership) — flagged Item or carrying an item class.
            if ((it.Flags & EntFlags.Item) == 0 && !it.ClassName.StartsWith("item_", System.StringComparison.Ordinal))
                continue;

            // hand the item to the player via the host seam (the pickup sound is part of that path).
            GiveItemHandler?.Invoke(it, actor);

            // QC: if(it.target != "" && it.target != "###item###") SUB_UseTargets(it, actor, trigger);
            if (!string.IsNullOrEmpty(it.Target) && it.Target != "###item###")
                MapMover.UseTargets(it, actor, null);
        }
    }

    // ===================================================================
    //  target_items (target/items.qc) — set/add items + resources + powerups
    // ===================================================================

    /// <summary>
    /// <c>spawnfunc(target_items)</c> — caches the give-token list (<c>.netname</c>) so a later use applies it.
    /// <para>
    /// QC target/items.qc:spawnfunc re-serializes the human token list into a normalized give-string with
    /// a per-spawnflag operator prefix (spawnflags 0 = set/give, 1 = max, 2 = min, 4 = minus/max dual).
    /// The port reproduces this normalization for spawnflags 0/1/2 by injecting the operator token before
    /// each non-numeric, non-operator name token in the raw <c>.netname</c>. Spawnflags==4 (dual minus/max
    /// prefix: item bits use "minus", resource values use "max") is not fully reproduced — left partial.
    /// </para>
    /// <para>
    /// If <c>.netname</c> begins with <c>"give "</c> (a pre-formatted give string from a QC-aware mapper
    /// or the cheat console), the "give " prefix is stripped so the remainder parses as a raw give-grammar
    /// string directly, matching QC's <c>if(argv(0) == "give") str = substring(netname, …)</c> branch.
    /// </para>
    /// </summary>
    public static void ItemsSetup(Entity this_)
    {
        this_.Use = ItemsUse;
        this_.ClassName = "target_items";
        // NOTE: loot-toucher delete is modelled in ItemsUse (see QC items.qc:8-26).

        // QC seeds the powerup durations from the balance cvars when unset (so a bare "strength" token grants
        // the default duration). Mirror that onto the world-item timer fields the applier reads.
        if (this_.StrengthFinished == 0f)
            this_.StrengthFinished = CvarOr("g_balance_powerup_strength_time", 30f);
        if (this_.InvincibleFinished == 0f)
            this_.InvincibleFinished = CvarOr("g_balance_powerup_invincible_time", 30f);
        if (this_.SpeedFinished == 0f)
            this_.SpeedFinished = CvarOr("g_balance_powerup_speed_time", 30f);
        if (this_.InvisibilityFinished == 0f)
            this_.InvisibilityFinished = CvarOr("g_balance_powerup_invisibility_time", 30f);
        if (this_.SuperweaponsFinished == 0f)
            this_.SuperweaponsFinished = CvarOr("g_balance_superweapons_time", 30f);

        // QC items.qc:spawnfunc(target_items): normalize .netname based on spawnflags.
        this_.NetName = NormalizeItemsNetname(this_.NetName, this_.SpawnFlags);

        MapMover.IndexRegister(this_);
    }

    // QC operator-prefix tokens — these are NOT name tokens; they set the op and continue without resetting.
    // "no" is also an operator (sets op=Max, val=0) but is unusual in target_items .netname; included here
    // so it is not double-prefixed if a mapper happens to write it.
    private static readonly System.Collections.Generic.HashSet<string> s_giveOpTokens =
        new(System.StringComparer.Ordinal) { "max", "min", "plus", "minus", "no" };

    // QC items.qc reserialization splits the give-tokens into two prefix classes for spawnflags 4
    // (itemprefix="minus " vs valueprefix="max "). The ITEM-class (itemprefix) tokens are the held-item BITS:
    // the unlimited_* flags, jetpack, fuel_regen (items.qc:124-132) and every weapon netname (items.qc:142).
    // Everything else — the powerup-duration names, resources, and buffs (items.qc:126-141) — is VALUE-class
    // (valueprefix). For spawnflags 1/2 itemprefix==valueprefix so the split is moot; it only matters for 4.
    private static readonly System.Collections.Generic.HashSet<string> s_itemBitNames =
        new(System.StringComparer.Ordinal)
        {
            "unlimited_ammo", "unlimited_weapon_ammo", "unlimited_superweapons", "jetpack", "fuel_regen",
        };

    /// <summary>
    /// QC <c>spawnfunc(target_items)</c> normalization (items.qc:47-155): re-serialize the raw
    /// <paramref name="netname"/> with the operator prefix dictated by <paramref name="spawnFlags"/>.
    /// <list type="bullet">
    ///   <item>If <paramref name="netname"/> starts with <c>"give "</c>: strip the prefix — the remainder
    ///     is already in give-grammar format (QC <c>if(argv(0)=="give") str = substring(netname,…)</c>).</item>
    ///   <item><paramref name="spawnFlags"/>==0 (set/give): no prefix — token list is already valid input
    ///     for <c>GiveItems.Apply</c> as written.</item>
    ///   <item><paramref name="spawnFlags"/>==1 (max/cap): inject <c>"max"</c> before each name token so
    ///     <c>GiveItems.Apply</c> uses <c>GiveOp.Max</c> — cap resources to at most the given value
    ///     (<c>MathF.Min(current, val)</c>). Useful for a draining/limiting trigger.</item>
    ///   <item><paramref name="spawnFlags"/>==2 (min/floor): inject <c>"min"</c> before each name token so
    ///     <c>GiveItems.Apply</c> uses <c>GiveOp.Min</c> — ensure resources are at least the given value
    ///     (<c>MathF.Max(current, val)</c>). Useful for a setup/ensure-minimum trigger.</item>
    ///   <item><paramref name="spawnFlags"/>==4 (minus/max dual): QC uses <c>itemprefix="minus "</c> for the
    ///     held-item bits (unlimited_*, jetpack, fuel_regen, weapons) and <c>valueprefix="max "</c> for the
    ///     resource/powerup-duration/buff values (items.qc:112-116). The port honors that split: item-bit name
    ///     tokens get <c>"minus"</c>, all other name tokens get <c>"max"</c>.</item>
    /// </list>
    /// <para>
    /// NOTE: this is an APPROXIMATION of QC's spawnfunc — QC parses the netname into <c>this.items</c> bits +
    /// resource fields and then re-serializes a canonical string from those (value tokens for resources come
    /// from the entity's <c>GetResource</c> fields, not the netname). The port instead reuses the netname's own
    /// value tokens and only injects the operator prefix, which is faithful for the common resource/powerup
    /// give-string but does not reproduce the bit-parse-then-reserialize for non-give-string inputs (documented
    /// partial gap; reproducing it needs the full items-bit/resource-field serialization subsystem).
    /// </para>
    /// </summary>
    public static string NormalizeItemsNetname(string? netname, int spawnFlags)
    {
        if (string.IsNullOrWhiteSpace(netname))
            return "";

        // QC items.qc:47-51: if argv(0)=="give", strip it and keep the rest as a raw give-string.
        // This handles a pre-formatted give-grammar string like "give 100 health 30 strength".
        const string GivePrefix = "give ";
        if (netname.StartsWith(GivePrefix, System.StringComparison.Ordinal))
            return netname.Substring(GivePrefix.Length).Trim();

        // spawnflags 0: no-op — the raw token list "N name N name …" is already valid GiveItems input
        // with OP_SET (the default), matching QC's itemprefix=="" / valueprefix=="" branch.
        if (spawnFlags == 0)
            return netname;

        // Determine the operator prefix(es) for spawnflags 1/2/4 (items.qc:97-121).
        // spawnflags 1 → itemprefix=valueprefix="max " (GiveOp.Max = MathF.Min(v0, val): cap to at most N).
        // spawnflags 2 → itemprefix=valueprefix="min " (GiveOp.Min = MathF.Max(v0, val): floor to at least N).
        // spawnflags 4 → itemprefix="minus " (held-item bits), valueprefix="max " (resource/duration values).
        // anything else → QC error("invalid spawnflags"); the port leaves the netname unchanged.
        string itemPrefix, valuePrefix;
        switch (spawnFlags)
        {
            case 1: itemPrefix = valuePrefix = "max"; break;
            case 2: itemPrefix = valuePrefix = "min"; break;
            case 4: itemPrefix = "minus"; valuePrefix = "max"; break;
            default: return netname;
        }

        // Rebuild the token list by injecting the prefix token before each name token (non-numeric,
        // non-operator). Numeric tokens (e.g. "30") and existing operator tokens are passed through as-is.
        string[] tokens = netname.Split((char[]?)null, System.StringSplitOptions.RemoveEmptyEntries);
        var sb = new System.Text.StringBuilder(netname.Length * 2);
        foreach (string tok in tokens)
        {
            if (sb.Length > 0)
                sb.Append(' ');

            // A token is "numeric" if it parses as a float (covers "100", "0", "30.5", "-1", etc.).
            bool isNumeric = float.TryParse(tok, System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out _);
            // An operator prefix token (max/min/plus/minus/no) must not be double-prefixed.
            bool isOp = s_giveOpTokens.Contains(tok);

            if (!isNumeric && !isOp)
            {
                // Name token: pick the QC prefix class. Held-item bits (unlimited_*, jetpack, fuel_regen, and
                // weapon netnames) use itemprefix; resources / powerup durations / buffs use valueprefix. For
                // spawnflags 1/2 the two are identical so the classification is a no-op.
                bool isItemBit = s_itemBitNames.Contains(tok)
                    || Weapons.ByName(tok) is not null;
                sb.Append(isItemBit ? itemPrefix : valuePrefix);
                sb.Append(' ');
            }

            sb.Append(tok);
        }

        return sb.ToString();
    }

    /// <summary>
    /// QC <c>target_items_use</c> + the give-token apply (a scoped GiveItems): give the activator the held-item
    /// flags, powerup status effects, and resources named in <c>.netname</c>. The weapon-set tokens (wr_init,
    /// per-weapon ammo) are out of scope (no Inventory token applier here). The loot-toucher delete branch is
    /// now modelled: an ITEM_IS_LOOT actor deletes itself (items.qc:8-13) and a player actor deletes all loot
    /// items whose .enemy points at them (items.qc:23-26 IL_EACH delete-by-enemy pass).
    /// </summary>
    private static void ItemsUse(Entity self, Entity actor)
    {
        // QC target_items_use items.qc:8-13:
        //   if(ITEM_IS_LOOT(actor)) { EXACTTRIGGER_TOUCH(this, trigger); delete(actor); return; }
        // A loot entity (dropped weapon / monster loot) that triggers target_items is simply deleted.
        // EXACTTRIGGER_TOUCH validates the touch geometry — the port skips it (target_items has no bmodel,
        // so the touch-validation is a no-op on the .use path, matching the QC comment "bones_was_here").
        if (actor.ItemIsLoot)
        {
            MapMover.RemoveEntity(actor);
            return;
        }

        if ((actor.Flags & EntFlags.Client) == 0 || MapMover.IsDead(actor))
            return;

        // QC items.qc:23-26: IL_EACH(g_items, it.enemy == actor && ITEM_IS_LOOT(it), { delete(it); })
        // For a player activator, also delete any loot items that are "owned by" (enemy == actor) them —
        // e.g. a weapon the player dropped before picking up from this target. Uses Api.Entities.All when
        // available (the live server path); no-ops on headless test fakes that don't supply a full entity list.
        if (Api.Services is not null && Api.Entities.All is { } allEnts)
        {
            List<Entity>? toLoot = null;
            foreach (Entity it in allEnts)
            {
                if (it.ItemIsLoot && ReferenceEquals(it.Enemy, actor))
                    (toLoot ??= new List<Entity>()).Add(it);
            }
            if (toLoot is not null)
                foreach (Entity it in toLoot)
                    MapMover.RemoveEntity(it);
        }

        bool gave = ApplyGiveTokens(self, actor, self.NetName);
        if (gave)
        {
            // QC: if(GiveItems(...)) centerprint(actor, this.message). The give has no sound in Base — route the
            // text through the centerprint seam, which delivers it to the activator's HUD via the CenterRaw
            // notification channel (→ CenterPrintPanel.Add), the same path chat /tell and other map .message text use.
            MapMover.Centerprint(actor, self.Message);
        }
    }

    /// <summary>
    /// QC <c>target_items_use</c>'s give (target/items.qc:28): <c>GiveItems(actor, 0, tokenize(netname))</c>.
    /// Now routes through the shared, op-aware <see cref="XonoticGodot.Common.Gameplay.GiveItems"/> (T35) so the full
    /// operator grammar (no/max/min/plus/minus + the all/allweapons/allammo aggregates) and the dual-rep weapon
    /// gives apply identically to the cheat <c>give</c> and <c>target_give</c>. <paramref name="self"/> is the
    /// target_items entity (its seeded powerup-duration fields drive the persistent <c>.items</c> template mode,
    /// not this direct give — QC passes only the raw tokens to GiveItems). Returns true if anything was applied.
    /// </summary>
    public static bool ApplyGiveTokens(Entity self, Entity actor, string? tokens)
    {
        if (string.IsNullOrWhiteSpace(tokens))
            return false;
        string[] argv = tokens.Split((char[]?)null, System.StringSplitOptions.RemoveEmptyEntries);
        return GiveItems.Apply(actor, argv) != 0;
    }

    // ===================================================================
    //  target_spawn (target/spawn.qc) — MINIMAL data-driven creator
    //  The full $-field reflection templating (putentityfieldstring / db field DB) has NO port analogue, so
    //  this is intentionally minimal: it spawns ON_MAPLOAD if flagged (the common "spawn one entity at load"
    //  use) and otherwise no-ops on use. Documented cut; do not sink XL effort into the reflection engine.
    // ===================================================================

    /// <summary>Host seam: create a fixed entity from a target_spawn's <c>.message</c> field string (optional).</summary>
    public static System.Action<Entity /*target_spawn*/, Entity? /*actor*/>? SpawnEntityHandler;

    /// <summary>
    /// <c>spawnfunc(target_spawn)</c> — MINIMAL: keeps the field-string in <c>.message</c> and, if ON_MAPLOAD,
    /// fires once at load via a deferred think. The $-templating field DB is NOT ported (see file header).
    /// </summary>
    public static void SpawnSetup(Entity this_)
    {
        this_.Use = SpawnUse;
        this_.Active = MapMover.ActiveActive;
        this_.ClassName = "target_spawn";

        if ((this_.SpawnFlags & MapMover.SpawnAllEntities) != 0) // ON_MAPLOAD (BIT(1))
        {
            this_.Think = e => SpawnUse(e, null!);
            this_.NextThink = MapMover.Now();
        }
        MapMover.IndexRegister(this_);
        // QC: this.reset = target_spawn_reset — dormant until reset infra exists.
    }

    /// <summary>QC <c>target_spawn_use</c> (minimal): delegate to the host seam; the templating engine is cut.</summary>
    private static void SpawnUse(Entity self, Entity? actor)
    {
        if (self.Active != MapMover.ActiveActive)
            return;
        SpawnEntityHandler?.Invoke(self, actor);
    }

    // ===================================================================
    //  func_door_secret (func/door_secret.qc) — slide-back-then-side secret door
    // ===================================================================

    public const int DoorSecretOpenOnce = 1 << 0; // DOOR_SECRET_OPEN_ONCE (stays open)
    public const int DoorSecret1stLeft = 1 << 1;  // DOOR_SECRET_1ST_LEFT
    public const int DoorSecret1stDown = 1 << 2;  // DOOR_SECRET_1ST_DOWN
    public const int DoorSecretNoShoot = 1 << 3;  // DOOR_SECRET_NO_SHOOT (only opened by trigger)
    public const int DoorSecretYesShoot = 1 << 4; // DOOR_SECRET_YES_SHOOT (shootable even if targeted)

    private static bool _secretDeathHooked;

    /// <summary>
    /// <c>spawnfunc(func_door_secret)</c> — a secret door that slides back (or down) then sideways when used or
    /// shot, returns after <c>.wait</c>, and (unless OPEN_ONCE) re-arms. Angle sets the slide direction.
    /// </summary>
    public static void DoorSecretSetup(Entity this_)
    {
        EnsureSecretDeathHook();

        if (this_.Dmg == 0f)
            this_.Dmg = 2f;

        // Magic formula: stash the angle as mangle, clear angles so the brush isn't rotated.
        this_.MAngle = this_.Angles;
        this_.Angles = Vector3.Zero;
        // QC sets classname = "door" so the shared door_blocked + per-entity event_damage apply. The port
        // replaced QC's PER-ENTITY .event_damage with a CLASSNAME-keyed Combat.Death dispatch (Doors.OnDeath
        // matches "door") and LinkDoors walks AllByClass("door"); both would wrongly grab a secret door. So we
        // use a DISTINCT classname here — the secret slide mechanics are identical (its own touch/blocked/use +
        // OnSecretDeath), this just keeps it out of the regular-door shared dispatch. (Faithful deviation forced
        // by the shared-dispatch architecture; see crossTaskNeeds.)
        this_.ClassName = "door_secret";
        if (!MapMover.InitMovingBrushTrigger(this_))
            return;

        // soundpack (QC: sounds > 0 -> medieval/metal plat sounds). Routed through the Wave-1 seam so the
        // promoted `sounds` map key is honored (the inline default-pack hardcode is gone).
        MapMover.ApplySecretSounds(this_);
        if (string.IsNullOrEmpty(this_.Noise))
            this_.Noise = "misc/talk.wav"; // sound on touch

        this_.Touch = SecretTouch;
        this_.Blocked = SecretBlocked;
        this_.Speed = 50f;
        this_.Use = SecretUse;
        this_.Active = MapMover.ActiveActive;

        // a secret with no targetname is always shootable.
        if (string.IsNullOrEmpty(this_.TargetName))
            this_.SpawnFlags |= DoorSecretYesShoot;

        if ((this_.SpawnFlags & DoorSecretYesShoot) != 0)
        {
            this_.SetResourceExplicit(ResourceType.Health, 10000f);
            this_.TakeDamage = DamageMode.Yes;
            // QC fd_secret_damage (door_secret.qc:63-66) opens the door on ANY hit, ignoring health — the 10000 HP
            // just ensures it never dies. So install the per-entity event_damage analogue (GtEventDamage, dispatched
            // by DamageSystem.EventDamage for non-player edicts) rather than the Combat.Death hook, which only fires
            // at health<1 and is therefore unreachable here.
            this_.GtEventDamage = (self, _, atk, _, _, _, _) => SecretUse(self, atk ?? self);
            this_.MaxHealthMover = 10000f;
        }

        this_.OldOrigin = this_.Origin;
        if (this_.Wait == 0f)
            this_.Wait = 5f; // seconds before closing

        // QC: this.reset = secret_reset; this.reset(this) — call the resetter once now (initial placement),
        // and leave it installed so GameWorld.ResetMapObjects re-arms it on a round/match restart.
        this_.Reset = SecretReset;
        SecretReset(this_);

        MapMover.IndexRegister(this_);
    }

    /// <summary>Subscribe once to <see cref="Combat.Death"/> so a shot-down shootable secret door opens.</summary>
    private static void EnsureSecretDeathHook()
    {
        if (_secretDeathHooked)
            return;
        _secretDeathHooked = true;
        Combat.Death.Add(OnSecretDeath);
    }

    /// <summary>
    /// <see cref="Combat.Death"/> handler (QC <c>fd_secret_damage</c>): a shootable secret "door" driven below 1
    /// HP opens. Restore its health so the kill path's HP&gt;=1 early-out keeps the brush intact, then open it.
    /// </summary>
    private static bool OnSecretDeath(ref DeathEvent ev)
    {
        Entity self = ev.Victim;
        // Only a shootable func_door_secret (its own distinct classname; see DoorSecretSetup) reacts here.
        if (self.ClassName != "door_secret" || self.Use != SecretUse)
            return false;
        if (self.TakeDamage == DamageMode.No)
            return false;
        self.SetResourceExplicit(ResourceType.Health, 10000f);
        SecretUse(self, ev.Attacker ?? self);
        return false; // non-exclusive
    }

    /// <summary>QC <c>fd_secret_use</c>: kick off the slide (back/down, then sideways) if at rest.</summary>
    private static void SecretUse(Entity self, Entity actor)
    {
        if (self.Active != MapMover.ActiveActive)
            return;

        self.SetResourceExplicit(ResourceType.Health, 10000f);
        // QC IL_PUSH(g_bot_targets) / .bot_attack — bot-waypoint only, skipped.

        // exit if still moving around...
        if (self.Origin != self.OldOrigin)
            return;

        string messageSave = self.Message;
        self.Message = ""; // no more message
        MapMover.UseTargets(self, actor, null); // fire all targets / killtargets
        self.Message = messageSave;

        self.Velocity = Vector3.Zero;

        if (!string.IsNullOrEmpty(self.Noise1))
            MapMover.Sound(self, SoundChannel.Item, self.Noise1);
        self.NextThink = self.LTime + 0.1f;

        int temp = 1 - (self.SpawnFlags & DoorSecret1stLeft); // 1 or -1
        QMath.AngleVectors(self.MAngle, out Vector3 vForward, out Vector3 vRight, out Vector3 vUp);

        if (self.TWidth == 0f)
        {
            if ((self.SpawnFlags & DoorSecret1stDown) != 0)
                self.TWidth = MathF.Abs(QMath.Dot(vUp, self.Size));
            else
                self.TWidth = MathF.Abs(QMath.Dot(vRight, self.Size));
        }

        if (self.TLength == 0f)
            self.TLength = MathF.Abs(QMath.Dot(vForward, self.Size));

        if ((self.SpawnFlags & DoorSecret1stDown) != 0)
            self.Pos1 = self.Origin - vUp * self.TWidth;        // dest1
        else
            self.Pos1 = self.Origin + vRight * (self.TWidth * temp);

        self.Pos2 = self.Pos1 + vForward * self.TLength;        // dest2
        MapMover.CalcMove(self, self.Pos1, MapMover.SpeedType.Linear, self.Speed, SecretMove1);
        if (!string.IsNullOrEmpty(self.Noise2))
            MapMover.Sound(self, SoundChannel.Item, self.Noise2);
    }

    /// <summary>QC <c>fd_secret_move1</c>: wait after the first (back/down) movement.</summary>
    private static void SecretMove1(Entity self)
    {
        self.NextThink = self.LTime + 1.0f;
        self.Think = SecretMove2;
        if (!string.IsNullOrEmpty(self.Noise3))
            MapMover.Sound(self, SoundChannel.Item, self.Noise3);
    }

    /// <summary>QC <c>fd_secret_move2</c>: start moving sideways.</summary>
    private static void SecretMove2(Entity self)
    {
        if (!string.IsNullOrEmpty(self.Noise2))
            MapMover.Sound(self, SoundChannel.Item, self.Noise2);
        MapMover.CalcMove(self, self.Pos2, MapMover.SpeedType.Linear, self.Speed, SecretMove3);
    }

    /// <summary>QC <c>fd_secret_move3</c>: wait open until it's time to go back (unless OPEN_ONCE / wait &lt; 0).</summary>
    private static void SecretMove3(Entity self)
    {
        if (!string.IsNullOrEmpty(self.Noise3))
            MapMover.Sound(self, SoundChannel.Item, self.Noise3);
        if ((self.SpawnFlags & DoorSecretOpenOnce) == 0 && self.Wait >= 0f)
        {
            self.NextThink = self.LTime + self.Wait;
            self.Think = SecretMove4;
        }
    }

    /// <summary>QC <c>fd_secret_move4</c>: move back sideways.</summary>
    private static void SecretMove4(Entity self)
    {
        if (!string.IsNullOrEmpty(self.Noise2))
            MapMover.Sound(self, SoundChannel.Item, self.Noise2);
        MapMover.CalcMove(self, self.Pos1, MapMover.SpeedType.Linear, self.Speed, SecretMove5);
    }

    /// <summary>QC <c>fd_secret_move5</c>: wait 1 second.</summary>
    private static void SecretMove5(Entity self)
    {
        self.NextThink = self.LTime + 1.0f;
        self.Think = SecretMove6;
        if (!string.IsNullOrEmpty(self.Noise3))
            MapMover.Sound(self, SoundChannel.Item, self.Noise3);
    }

    /// <summary>QC <c>fd_secret_move6</c>: move forward back to the original spot.</summary>
    private static void SecretMove6(Entity self)
    {
        if (!string.IsNullOrEmpty(self.Noise2))
            MapMover.Sound(self, SoundChannel.Item, self.Noise2);
        MapMover.CalcMove(self, self.OldOrigin, MapMover.SpeedType.Linear, self.Speed, SecretDone);
    }

    /// <summary>QC <c>fd_secret_done</c>: re-arm a shootable secret once it's closed.</summary>
    private static void SecretDone(Entity self)
    {
        if ((self.SpawnFlags & DoorSecretYesShoot) != 0)
        {
            self.SetResourceExplicit(ResourceType.Health, 10000f);
            self.TakeDamage = DamageMode.Yes;
        }
        if (!string.IsNullOrEmpty(self.Noise3))
            MapMover.Sound(self, SoundChannel.Item, self.Noise3);
    }

    /// <summary>QC <c>secret_blocked</c>: a blocked secret door waits a debounce (no crush damage in QC's port).</summary>
    private static void SecretBlocked(Entity self, Entity blocker)
    {
        if (MapMover.Now() < self.DoorFinished)
            return;
        self.DoorFinished = MapMover.Now() + 0.5f;
        // QC leaves the T_Damage call commented out — no crush damage.
    }

    /// <summary>QC <c>secret_touch</c>: a creature touching the closed secret hears the touch sound (debounced).</summary>
    private static void SecretTouch(Entity self, Entity toucher)
    {
        if (!MapMover.IsCreature(toucher))
            return;
        if (self.DoorFinished > MapMover.Now())
            return;
        if (self.Active != MapMover.ActiveActive)
            return;

        self.DoorFinished = MapMover.Now() + 2f;

        if (!string.IsNullOrEmpty(self.Message))
        {
            // QC door_secret.qc:163-166: if(IS_CLIENT(toucher)) centerprint(toucher, this.message);
            // play2(toucher, this.noise) — a per-recipient 2D send (VOL_BASE/ATTEN_NONE), not positional.
            MapMover.Centerprint(toucher, self.Message);
            MapMover.Play2(toucher, self.Noise);
        }
    }

    /// <summary>QC <c>secret_reset</c>: snap home, stop, re-arm shootable, clear the think (dormant until reset infra).</summary>
    public static void SecretReset(Entity self)
    {
        if ((self.SpawnFlags & DoorSecretYesShoot) != 0)
        {
            self.SetResourceExplicit(ResourceType.Health, 10000f);
            self.TakeDamage = DamageMode.Yes;
        }
        MapMover.SetOrigin(self, self.OldOrigin);
        self.Think = null;
        self.NextThink = 0f;
        self.Active = MapMover.ActiveActive;
    }

    // ---- helpers -------------------------------------------------------

    private static float CvarOr(string name, float fallback)
    {
        if (Api.Services is null) return fallback;
        float v = Api.Cvars.GetFloat(name);
        return v != 0f ? v : fallback;
    }
}
