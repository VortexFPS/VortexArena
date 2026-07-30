using System.Numerics;
using XonoticGodot.Common.Framework;
using XonoticGodot.Common.Math;
using XonoticGodot.Common.Services;
using XonoticGodot.Common.Gameplay.Damage;

namespace XonoticGodot.Common.Gameplay;

/// <summary>
/// Shared monster-subsystem support that doesn't belong on the per-think driver itself: the
/// monster-specific status effects (Webbed / Shield / SpawnShield) that QC registered in
/// <c>common/mutators/mutator/status_effects</c>, plus the deep ability primitives that several
/// monsters reuse — projectile homing (the Seeker-style steer in M_Mage_Attack_Spike_Think), the
/// burning application (Fire_AddDamage), the spider web slow, the golem's chained lightning zaps, and
/// the shared loot-drop / danger-avoidance helpers from sv_monsters.qc.
///
/// This is a NEW prefixed file (task: "New per-entity state → … a new prefixed file (Framework
/// namespace)"). It deliberately does NOT modify <see cref="Entity"/> or the existing
/// <see cref="StatusEffectsCatalog"/> source — the three monster effects self-register here on first use
/// via the public <see cref="Registry{T}"/> API so the catalog gains them without editing its file.
/// </summary>
public static class MonsterFramework
{
    // ====================================================================================
    // Monster status effects (STATUSEFFECT_Webbed / _Shield / _SpawnShield)
    // ====================================================================================
    //
    // The base StatusEffectsCatalog.RegisterAll() seeds frozen/burning/buffs. The monster set adds three
    // more; we register them lazily (idempotent) so they exist whether or not the host called RegisterAll
    // for them, mirroring QC's REGISTER_STATUSEFFECT accumulation.

    private static StatusEffectDef? _webbed, _shield, _spawnShield, _burning;
    private static bool _ensured;

    private static void Ensure()
    {
        if (_ensured && _webbed is not null) return;

        // Reuse the catalog's burning if RegisterAll ran; otherwise register it too.
        _burning = StatusEffectsCatalog.ByName("burning");
        if (_burning is null)
        {
            _burning = new StatusEffectDef("burning");
            Registry<StatusEffectDef>.Register(_burning);
        }

        _webbed = StatusEffectsCatalog.ByName("webbed") ?? Register("webbed");
        _shield = StatusEffectsCatalog.ByName("shield") ?? Register("shield");
        _spawnShield = StatusEffectsCatalog.ByName("spawnshield") ?? Register("spawnshield");
        _ensured = true;

        static StatusEffectDef Register(string name)
        {
            var d = new StatusEffectDef(name);
            Registry<StatusEffectDef>.Register(d);
            return d;
        }
    }

    /// <summary>STATUSEFFECT_Webbed (spiderweb mutator): halves movement; applied by the spider's web.</summary>
    public static StatusEffectDef Webbed { get { Ensure(); return _webbed!; } }

    /// <summary>STATUSEFFECT_Shield (mage): blocks/greatly reduces incoming damage for its duration.</summary>
    public static StatusEffectDef Shield { get { Ensure(); return _shield!; } }

    /// <summary>STATUSEFFECT_SpawnShield: brief post-spawn invulnerability (Monster_Spawn).</summary>
    public static StatusEffectDef SpawnShield { get { Ensure(); return _spawnShield!; } }

    /// <summary>STATUSEFFECT_Burning (shared with the global catalog): the wyvern fireball ignites in radius.</summary>
    public static StatusEffectDef Burning { get { Ensure(); return _burning!; } }

    public static bool Active(StatusEffectDef def, Entity e) => StatusEffectsCatalog.Has(e, def);

    public static void ApplyFor(StatusEffectDef def, Entity e, float duration, float strength = 1f, Entity? source = null)
        => StatusEffectsCatalog.Apply(e, def, duration, strength, source);

    // ====================================================================================
    // Fire / burning (common/mutators/mutator/instagib? no — common/effects Fire_AddDamage)
    // ====================================================================================

    /// <summary>
    /// Port of <c>Fire_AddDamage</c> (server, common burning) reduced to the headless essentials: ignite a
    /// target so it takes <paramref name="totalDamage"/> spread over <paramref name="burnTime"/> seconds.
    /// Routes through <see cref="StatusEffectsCatalog.FireAddDamage"/> — the single faithful ignition entry
    /// point — so the burn tick (which deals <c>fire_damagepersec * frametime</c>) uses the correct raw-DPS
    /// convention and the QC overlap LEMMA / deathtype-owner attribution apply uniformly with the other
    /// ignition sites (fireball/napalm).
    /// </summary>
    public static void AddFireDamage(Entity targ, Entity? owner, float totalDamage, float burnTime, string deathType)
    {
        if (targ.TakeDamage == DamageMode.No || targ.DeadState != DeadFlag.No || targ.Health <= 0f)
            return;
        StatusEffectsCatalog.FireAddDamage(targ, owner, totalDamage, burnTime, deathType);
    }

    // ====================================================================================
    // Projectile homing (M_Mage_Attack_Spike_Think — copied from W_Seeker_Think)
    // ====================================================================================

    /// <summary>
    /// Per-frame homing steer for a guided projectile (the mage spike). Faithful port of the seeker math in
    /// <c>M_Mage_Attack_Spike_Think</c>: accel/decel the speed toward <paramref name="speedMax"/>, then bend
    /// the velocity toward the (bbox-center of the) enemy by <paramref name="turnRate"/>, optionally doing a
    /// "smart" obstacle-avoidance trace that mixes in the surface normal. Drops the enemy if it died/became
    /// non-aim-damageable. Call this from the projectile's think; returns false if the projectile should
    /// explode now (lifetime elapsed or owner/enemy gone).
    /// </summary>
    public static bool HomeProjectile(Entity proj, float deathTime, float turnRate, float accel, float decel,
        float speedMax, bool smart, float smartMinDist, float smartTraceMin, float smartTraceMax)
    {
        float now = MonsterAI.Now;
        if (now > deathTime
            || (proj.Enemy is not null && proj.Enemy.Health <= 0f)
            || proj.Owner is null || proj.Owner.Health <= 0f)
            return false;

        float dt = MonsterAI.FrameTime;
        if (dt <= 0f) dt = 0.05f;

        float spd = proj.Velocity.Length();
        // QC bound(min, value, max) with min=decel-reduced, value=speed_max, max=accel-raised.
        spd = QMath.Bound(spd - decel * dt, speedMax, spd + accel * dt);

        // Drop a dead / undamageable enemy (QC: takedamage != DAMAGE_AIM || IS_DEAD).
        if (proj.Enemy is not null
            && (proj.Enemy.TakeDamage != DamageMode.Aim || proj.Enemy.DeadState != DeadFlag.No))
            proj.Enemy = null;

        if (proj.Enemy is not null)
        {
            Entity e = proj.Enemy;
            Vector3 eorg = 0.5f * (e.AbsMin + e.AbsMax);
            Vector3 desiredDir = QMath.Normalize(eorg - proj.Origin);
            Vector3 oldDir = QMath.Normalize(proj.Velocity);

            if (smart && (eorg - proj.Origin).Length() > smartMinDist)
            {
                // Trace ahead the cheaper of (along current dir by .wait) vs (straight to enemy).
                float wait = proj.LTime; // reuse LTime as the QC .wait adaptive trace length scratch
                Vector3 traceEnd;
                if ((proj.Origin + oldDir * wait).LengthSquared() < (eorg - proj.Origin).LengthSquared())
                    traceEnd = proj.Origin + oldDir * wait;
                else
                    traceEnd = eorg;

                TraceResult tr = Api.Trace.Trace(proj.Origin, Vector3.Zero, Vector3.Zero, traceEnd,
                    MoveFilter.Normal, proj);
                proj.LTime = QMath.Bound(smartTraceMin, (proj.Origin - tr.EndPos).Length(), smartTraceMax);

                // Weight turning more when an obstacle is close (QC mixes plane normal by 1-fraction).
                desiredDir = QMath.Normalize(
                    ((tr.PlaneNormal * (1f - tr.Fraction)) + (desiredDir * tr.Fraction)) * 0.5f);
            }

            Vector3 newDir = QMath.Normalize(oldDir + desiredDir * turnRate);
            proj.Velocity = newDir * spd;
        }
        else
        {
            // No target: keep flying straight at the (possibly adjusted) speed.
            Vector3 dir = QMath.Normalize(proj.Velocity);
            if (dir != Vector3.Zero) proj.Velocity = dir * spd;
        }

        proj.Angles = QMath.VecToAngles(proj.Velocity);
        return true;
    }

    // ====================================================================================
    // Golem chained lightning zaps (M_Golem_Attack_Lightning_Explode)
    // ====================================================================================

    /// <summary>
    /// Port of the FOREACH_ENTITY_RADIUS zap loop in <c>M_Golem_Attack_Lightning_Explode</c>: every
    /// damageable entity (other than the owner) within <paramref name="zapRadius"/> of <paramref name="origin"/>
    /// is arced for <paramref name="zapDamage"/> (×skillmod) through the real damage pipeline. The CSQC
    /// lightning arc beam (te_csqc_lightningarc) is client-render only — deferred.
    /// </summary>
    public static void ChainedZaps(Entity inflictor, Entity? owner, Vector3 origin, float zapRadius,
        float zapDamage, float skillMod, string deathType)
    {
        if (Api.Services is null) return;
        foreach (Entity it in Api.Entities.FindInRadius(origin, zapRadius))
        {
            if (it == owner || it == inflictor) continue;
            if (it.TakeDamage == DamageMode.No) continue;
            // te_csqc_lightningarc(origin, it.origin) — the jagged bolt the client draws to each zapped target.
            EffectEmitter.TeCsqcLightningArc(origin, it.Origin);
            Combat.Damage(it, inflictor, owner, zapDamage * skillMod, deathType, it.Origin, Vector3.Zero);
        }
    }

    // ====================================================================================
    // Loot drop (monster_dropitem, sv_monsters.qc)
    // ====================================================================================

    /// <summary>
    /// Port of <c>monster_dropitem</c> (sv_monsters.qc): on death, pick a real item from the monster's loot list
    /// (golem <c>"health_mega electro"</c>) via <see cref="ItemRandomFromList"/> and spawn it as a tossed loot
    /// entity that pops up + out of the corpse and despawns after the drop lifetime. Honors the
    /// g_monsters_drop_time gate, the .candrop toggle, and the miniboss-loot override. The chosen def is spawned
    /// through the real item pipeline (<see cref="StartItem.SpawnLoot"/>), so a health_mega heals and an electro
    /// loot grants the weapon — matching QC's <c>Item_Initialise</c> on the loot edict.
    /// </summary>
    public static void DropItem(Entity self, MonsterAI.MonsterState st, Entity? attacker)
    {
        if (!st.CanDrop) return;
        float dropTime = MonsterAI.Cvar("g_monsters_drop_time", 10f);
        if (dropTime <= 0f) return;

        string itemList = st.MonsterLoot;
        if (st.IsMiniboss)
            itemList = MonsterAI.CvarString("g_monsters_miniboss_loot", "vortex");

        if (Api.Services is null) return;

        // QC sv_monsters.qc:51-52: MUTATOR_CALLHOOK(MonsterDropItem, this, itemlist, attacker);
        //    itemlist = M_ARGV(1, string); — mutators (instagib) can override or clear the drop list.
        itemList = MutatorHooks.FireMonsterDropItem(self, itemList, attacker);
        if (string.IsNullOrEmpty(itemList)) return;

        // QC: entity loot_itemdef = Item_RandomFromList(itemlist); if (!loot_itemdef) return;
        Pickup? def = ItemRandomFromList(itemList);
        if (def is null) return;

        // QC: e = spawn(); e.monster_item = true; ITEM_SET_LOOT; e.itemdef = loot_itemdef;
        //     setorigin(e, CENTER_OR_VIEWOFS(this)); e.velocity = randomvec()*175 + '0 0 325';
        //     e.lifetime = autocvar_g_monsters_drop_time; Item_Initialise(e);
        Entity e = Api.Entities.Spawn();
        e.Owner = self;
        // CENTER_OR_VIEWOFS(this) for a non-player monster = origin + (mins+maxs)*0.5 (server/utils.qh:31).
        Vector3 center = self.Origin + (self.Mins + self.Maxs) * 0.5f;
        Api.Entities.SetOrigin(e, center);
        e.OldOrigin = center;
        StartItem.SpawnLoot(e, def, dropTime); // MOVETYPE_TOSS + lifetime despawn (QC Item_Initialise loot path)
        if (e.IsFreed) return; // NODROP / FilterItem removed it (faithful: no loot drops)
        // randomvec()*175 + '0 0 325' (QC) — deterministic via Prandom; set AFTER SpawnLoot so it isn't clobbered.
        e.Velocity = Prandom.Vec() * 175f + new Vector3(0, 0, 325);
    }

    /// <summary>
    /// Port of <c>Item_RandomFromList</c> (server/items/spawning.qc:71): pick one item def (weapon OR pickup) at
    /// random — equal weight — from a space-separated loot list. A token matches a weapon by netname (excluding
    /// WEP_FLAG_MUTATORBLOCKED) or an allowed item by netname; the literal token <c>"random"</c> matches any
    /// normal, non-hidden, non-superweapon weapon and any normal, non-powerup item (QC's RandomSelection over the
    /// catalogs). Returns the chosen <see cref="Pickup"/> (a weapon resolves to its <see cref="WeaponPickup"/>),
    /// or null if the list is empty / nothing matched.
    /// </summary>
    public static Pickup? ItemRandomFromList(string itemList)
    {
        if (string.IsNullOrEmpty(itemList)) return null;

        var candidates = new List<Pickup>();
        foreach (string item in itemList.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            // FOREACH(Weapons, it != WEP_Null && !MUTATORBLOCKED): netname match, or "random" -> normal weapons.
            foreach (Weapon w in Weapons.All)
            {
                if ((w.SpawnFlags & WeaponFlags.MutatorBlocked) != 0) continue;
                bool match = w.NetName == item
                    || (item == "random"
                        && (w.SpawnFlags & WeaponFlags.Normal) != 0
                        && (w.SpawnFlags & WeaponFlags.Hidden) == 0
                        && (w.SpawnFlags & WeaponFlags.SuperWeapon) == 0);
                if (match) candidates.Add(ItemSpawnFuncs.PickupFor(w));
            }
            // FOREACH(Items, Item_IsDefinitionAllowed): netname match, or "random" -> normal non-powerup items.
            foreach (Pickup p in Items.All)
            {
                if (!p.ItemDef.IsAllowed) continue;
                bool match = p.NetName == item
                    || (item == "random"
                        && (p.ItemDef.SpawnFlags & GameItemSpawnFlag.Normal) != 0
                        && !p.IsPowerup);
                if (match) candidates.Add(p);
            }
        }

        if (candidates.Count == 0) return null;
        // RandomSelection equal-weight pick (deterministic via Prandom).
        return candidates[Prandom.RangeInt(0, candidates.Count)];
    }

    // ====================================================================================
    // Danger avoidance (Monster_CheckDanger, sv_monsters.qc)
    // ====================================================================================

    /// <summary>
    /// Port of <c>Monster_CheckDanger</c> (sv_monsters.qc), ground branch reduced to the headless core:
    /// trace down ahead of the monster and report a danger level &gt; 0 for a deadly fall (sky / large drop)
    /// or hazardous fluid (lava/slime) ahead. Used by <see cref="MonsterAI.Move"/> to brake and re-path.
    /// 0 = safe, 1 = sky below, 2 = lethal fall, 3 = lava/slime.
    /// </summary>
    public static int CheckDanger(Entity self, Vector3 dstAhead)
    {
        if (Api.Services is null) return 0;

        bool flyOrSwim = (self.Flags & (EntFlags.Fly | EntFlags.Swim)) != 0;
        if (flyOrSwim)
        {
            // Flyers: look straight ahead; only the skybox below or hazardous fluid is "danger".
            TraceResult tr = Api.Trace.Trace(self.Origin + self.ViewOfs, Vector3.Zero, Vector3.Zero, dstAhead,
                MoveFilter.Normal, null);
            if (tr.EndPos.Z < self.Origin.Z + self.Mins.Z && IsSky(tr))
                return 1;
            int sc = Api.Trace.PointContents(tr.EndPos + new Vector3(0, 0, 1));
            if (sc != (int)Contents.Solid)
            {
                if (sc == (int)Contents.Lava || sc == (int)Contents.Slime) return 3;
                // 4) a trigger_hurt volume in the path ahead (QC tracebox_hits_trigger_hurt(dst_ahead, mins,
                //    maxs, trace_endpos)).
                if (HitsTriggerHurt(dstAhead, self.Mins, self.Maxs, tr.EndPos))
                    return 4;
            }
            return 0;
        }

        Vector3 dstDown = dstAhead - new Vector3(0, 0, 3000);
        TraceResult down = Api.Trace.Trace(dstAhead, Vector3.Zero, Vector3.Zero, dstDown, MoveFilter.Normal, null);
        if (down.EndPos.Z < self.Origin.Z + self.Mins.Z)
        {
            if (IsSky(down)) return 1;
            // Ignore probably-non-fatal falls when chasing an enemy; only small falls when wandering.
            float allowedDrop = self.Enemy is not null ? 1024f : 100f;
            if (down.EndPos.Z < (self.Origin.Z + self.Mins.Z) - allowedDrop)
                return 2;
            int sc = Api.Trace.PointContents(down.EndPos + new Vector3(0, 0, 1));
            if (sc != (int)Contents.Solid)
            {
                if (sc == (int)Contents.Lava || sc == (int)Contents.Slime) return 3;
                // 4) a trigger_hurt volume in the fall column (QC tracebox_hits_trigger_hurt(dst_ahead, mins,
                //    maxs, trace_endpos)).
                if (HitsTriggerHurt(dstAhead, self.Mins, self.Maxs, down.EndPos))
                    return 4;
            }
        }
        return 0;
    }

    /// <summary>
    /// QC <c>tracebox_hits_trigger_hurt(start, mins, maxs, end)</c> (common/mapobjects/trigger/hurt.qc:78): does
    /// the box <paramref name="mins"/>/<paramref name="maxs"/> swept from <paramref name="start"/> to
    /// <paramref name="end"/> overlap any <c>trigger_hurt</c> volume? QC walks the trigger_hurt linked list
    /// calling <c>tracebox_hits_box</c> (a swept-AABB vs box slab test). Mirrors the bot's BotDanger.HitsTriggerHurt
    /// (server) since Common cannot reference Server; same Minkowski-expand-then-slab-clip math.
    /// </summary>
    private static bool HitsTriggerHurt(Vector3 start, Vector3 mins, Vector3 maxs, Vector3 end)
    {
        if (Api.Services is null) return false;
        foreach (Entity e in Api.Entities.FindByClass("trigger_hurt"))
        {
            if (e.IsFreed) continue;
            if (e.AbsMin == e.AbsMax) continue; // unlinked/degenerate volume
            // QC tracebox_hits_box(start, mins, maxs, end, absmin, absmax)
            //   = trace_hits_box(start, end, absmin - maxs, absmax - mins)
            if (TraceHitsBox(start, end, e.AbsMin - maxs, e.AbsMax - mins))
                return true;
        }
        return false;
    }

    /// <summary>QC <c>trace_hits_box(start, end, thmi, thma)</c> (common/util.qc:2219): ray-vs-box slab clip.</summary>
    private static bool TraceHitsBox(Vector3 start, Vector3 end, Vector3 thmi, Vector3 thma)
    {
        end -= start;
        thmi -= start;
        thma -= start;
        float a0 = 0f, a1 = 1f;
        if (!HitsBox1D(end.X, thmi.X, thma.X, ref a0, ref a1)) return false;
        if (!HitsBox1D(end.Y, thmi.Y, thma.Y, ref a0, ref a1)) return false;
        if (!HitsBox1D(end.Z, thmi.Z, thma.Z, ref a0, ref a1)) return false;
        return true;
    }

    /// <summary>QC <c>trace_hits_box_1d</c> (common/util.qc:2197): one-axis slab clamp of the [a0,a1] interval.</summary>
    private static bool HitsBox1D(float end, float thmi, float thma, ref float a0, ref float a1)
    {
        if (end == 0f)
        {
            if (0f < thmi) return false;
            if (0f > thma) return false;
        }
        else
        {
            a0 = MathF.Max(a0, MathF.Min(thmi / end, thma / end));
            a1 = MathF.Min(a1, MathF.Max(thmi / end, thma / end));
            if (a0 > a1) return false;
        }
        return true;
    }

    private static bool IsSky(TraceResult tr)
    {
        // Q3SURFACEFLAG_SKY == BIT(2) == 4 in DP; also treat the legacy CONTENT_SKY hit as sky.
        const int Q3SurfaceFlagSky = 4;
        return (tr.DpHitQ3SurfaceFlags & Q3SurfaceFlagSky) != 0;
    }
}
