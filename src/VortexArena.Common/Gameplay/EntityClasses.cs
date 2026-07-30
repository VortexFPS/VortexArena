using XonoticGodot.Common.Framework;

namespace XonoticGodot.Common.Gameplay;

/// <summary>Base monster descriptor (QC CLASS(Monster), common/monsters/). Registered into <see cref="Monsters"/>.</summary>
public abstract partial class Monster : IRegistered
{
    public int RegistryId { get; set; }
    public string NetName = "";
    public string DisplayName = "";
    public string? Model;
    public float StartHealth;
    public float Damage;
    public float Speed;
    public string RegistryName => NetName;

    /// <summary>
    /// Monsterpedia flavor text — QC <c>METHOD(&lt;Monster&gt;, describe, string(...))</c> (MENUQC), e.g.
    /// <c>golem.qc:308</c>. The QC <c>describe()</c> builds its string with <c>PAGE_TEXT_INIT()</c> + one or more
    /// <c>PAR(...)</c> paragraphs joined by <c>"\n\n"</c> (lib/string.qh:659) and returns it for the menu's
    /// monster info page. The port has no monster info-page UI yet, so this is surfaced as queryable descriptor
    /// data (the same status as <see cref="DisplayName"/>, which also has no monster-menu consumer). Empty when a
    /// descriptor hasn't declared its <c>describe()</c> text.
    /// </summary>
    public virtual string Description => "";

    /// <summary>
    /// The voice cues actually DEFINED (uncommented) in this monster's model <c>.sounds</c> file — the
    /// faithful gate for QC <c>Monster_Sound</c>: a cue whose sample line is commented out (or whose model
    /// ships no <c>.sounds</c> file at all) yields an empty sample, so <c>sound7</c> plays nothing. The port
    /// mirrors that here by only emitting <c>monsters/&lt;name&gt;_&lt;cue&gt;.wav</c> for a cue in this set.
    /// <c>null</c> means "not audited — play every cue" (legacy behaviour, preserved for descriptors that
    /// haven't declared their table yet); an empty set means the monster is fully silent.
    /// </summary>
    public System.Collections.Generic.HashSet<string>? SoundCues;

    /// <summary>
    /// Sub-directory under <c>sound/monsters/</c> that holds this monster's voice cues, matching the base path
    /// in the model <c>.sounds</c> file (QC <c>GlobalSound</c> resolves <c>sound/monsters/&lt;dir&gt;/&lt;cue&gt;</c>).
    /// Defaults to the monster's <see cref="NetName"/> (golem -> <c>sound/monsters/golem/&lt;cue&gt;</c>).
    /// </summary>
    public virtual string SoundDir => NetName;

    /// <summary>
    /// File extension for this monster's voice cues. The golem ships <c>.wav</c>; the zombie ships <c>.ogg</c>.
    /// </summary>
    public virtual string SoundExt => ".wav";

    /// <summary>
    /// Group/variant count for a voice cue, mirroring the trailing number in the model <c>.sounds</c> line
    /// (QC <c>GlobalSound</c> picks a random <c>1..count</c> suffix). <c>0</c> (the default) means the bare cue
    /// name with no number — e.g. golem <c>death 3</c> -> <c>death1/2/3</c>, golem <c>sight 0</c> -> <c>sight</c>.
    /// </summary>
    public virtual int SoundCueCount(string cue) => 0;

    /// <summary>
    /// Pain-window length (QC <c>mr_pain</c>: <c>actor.pain_finished = time + N</c>): how long the pain
    /// reaction/anim holds before the monster may re-pain or resume walk anims. The generic
    /// <c>METHOD(Monster, mr_pain)</c> never bumps <c>pain_finished</c>, but most ported monsters reuse the
    /// zombie's 0.34s as the baseline; a monster that sets a different window in its <c>mr_pain</c> override
    /// (e.g. the wyvern / golem at 0.5s) returns it here.
    /// </summary>
    public virtual float PainWindow => 0.34f;

    /// <summary>
    /// Per-monster MD3 frame-group start index for a logical animation phase (QC <c>mr_anim</c>: this monster's
    /// <c>actor.anim_idle/walk/run/pain1/shoot/die1/die2</c> frame groups). Translating the shared
    /// <c>MonsterState.Anim</c> phase into the concrete networked <c>Entity.Frame</c> here is what makes the
    /// model actually play the named frame groups client-side (the frame is networked and the
    /// <c>ModelAnimator</c> follows it, CSQCMODEL_AUTOUPDATE). <paramref name="die2"/> selects the landed-corpse
    /// pose for monsters that split death into a falling (die1) + landed (die2) anim.
    /// Returns <c>null</c> for descriptors that haven't declared their <c>mr_anim</c> table yet, leaving
    /// <c>Entity.Frame</c> untouched (the movement-derived heuristic / frame 0 — no regression).
    /// </summary>
    public virtual float? AnimFrame(MonsterAnimPhase phase, bool die2) => null;

    /// <summary>
    /// Variant-aware frame lookup (QC <c>setanim(random ? anim_pain1 : anim_pain2)</c>): like
    /// <see cref="AnimFrame(MonsterAnimPhase,bool)"/> but with a per-phase <paramref name="variant"/> index the
    /// driver chose when the phase was (re)entered, letting a monster alternate between two interchangeable
    /// groups (golem pain1/pain2, melee2/melee3). The default ignores the variant and forwards to the 2-arg
    /// overload, so descriptors that don't use variants need no change.
    /// </summary>
    public virtual float? AnimFrame(MonsterAnimPhase phase, bool die2, int variant) => AnimFrame(phase, die2);

    /// <summary>
    /// Called once when the monster dies (QC <c>mr_death</c>) to choose the death animation variant.
    /// Returns <c>true</c> to select die2, <c>false</c> to select die1. The default is <c>false</c>
    /// (die1 always, correct for monsters that keep the falling → landed split via DeadThink).
    /// The zombie overrides this to roll <c>random() &gt; 0.5</c>, matching Base's immediate die1/die2
    /// pick in <c>mr_death</c>. The result is stored in <see cref="MonsterAI.MonsterState.DeathLanded"/>
    /// so <see cref="MonsterAI.DriveAnimFrame"/> routes it to the descriptor's <c>AnimFrame(die2)</c>.
    /// </summary>
    public virtual bool RollDeathVariant() => false;

    /// <summary>
    /// QC <c>(this.spawnflags &amp; MONSTER_SIZE_QUAKE)</c> (monster.qh, BIT for the Quake-imported monsters): this
    /// monster's model was authored at the original Quake scale and Xonotic enlarges it 1.3x at spawn when
    /// <c>g_monsters_quake_resize</c> is set (Monster_Spawn_Setup). The port's <see cref="MonsterAI.ApplySize"/>
    /// reads this to scale the COLLISION bbox (mins/maxs) 1.3x on a non-respawn spawn; the matching visual
    /// <c>scale *= 1.3</c> render enlargement needs a networked entity render-scale field the port doesn't have
    /// (see the unportable note). None of the five stock port monsters carry the flag (zombie/golem/mage/spider/
    /// wyvern are all Xonotic-native), so this is wired for parity with a future Quake-imported monster, latent
    /// today exactly as in Base's stock set. Default false.
    /// </summary>
    public virtual bool SizeQuake => false;

    /// <summary>
    /// QC <c>this.monsterdef.spawnflags &amp; MON_FLAG_RANGED</c> (monster.qh:10, BIT(9) "monster shoots
    /// projectiles"): whether this monster has a ranged attack. Used by <see cref="MonsterAI.ValidTarget"/> to
    /// reject a vehicle target for a melee-only monster (QC sv_monsters.qc:108 — "melee vs vehicle is useless").
    /// Base flags: Golem/Mage/Spider/Wyvern carry MON_FLAG_RANGED; the Zombie is melee-only (no flag), so it
    /// alone returns false. Defaults true (most monsters have some ranged option); the zombie overrides to false.
    /// </summary>
    public virtual bool IsRanged => true;

    /// <summary>Initialize a spawned monster entity.</summary>
    public virtual void Spawn(Entity e) { }
    /// <summary>Per-think AI step.</summary>
    public virtual void Think(Entity e) { }
    /// <summary>Attack a target.</summary>
    public virtual void Attack(Entity e, Entity target) { }
    /// <summary>
    /// MENUQC monster description text (QC <c>METHOD(Monster, describe)</c>): the multi-paragraph flavor prose
    /// for the monster. Used in UI tooltips and monster-guide displays. Returns <c>null</c> for descriptors
    /// without a describe method.
    /// </summary>
    public virtual string? Describe() => null;
}

/// <summary>
/// Logical monster animation phase, shared across descriptors (mirrors <c>MonsterAI.MonsterAnim</c>). Lives on
/// the descriptor layer so a monster's <see cref="Monster.AnimFrame"/> can map it to a concrete MD3 frame group.
/// </summary>
public enum MonsterAnimPhase { Idle, Walk, Run, Attack, Pain, Death, Spawn, Block, BlockEnd, Shoot }

/// <summary>Base turret descriptor (QC CLASS(Turret), common/turrets/). Registered into <see cref="Turrets"/>.</summary>
public abstract partial class Turret : IRegistered
{
    public int RegistryId { get; set; }
    public string NetName = "";
    public string DisplayName = "";
    public string? Model;
    /// <summary>QC <c>head_model</c> ATTRIB (e.g. fusionreactor.qh: <c>"models/turrets/reactor.md3"</c>) — the
    /// separate spinning head-bone model carried on top of the base body. Null for turrets without a head model.
    /// No client turret render integrates it yet (whole-family presentation gap), but it is carried as data so the
    /// identity matches Base.</summary>
    public string? HeadModel;
    public float StartHealth;
    public float Range;
    public string RegistryName => NetName;

    public virtual void Spawn(Entity e) { }
    public virtual void Think(Entity e) { }
    public virtual bool ValidTarget(Entity self, Entity target) => true;
}

/// <summary>Base vehicle descriptor (QC CLASS(Vehicle), common/vehicles/). Registered into <see cref="Vehicles"/>.</summary>
public abstract partial class Vehicle : IRegistered
{
    public int RegistryId { get; set; }
    public string NetName = "";
    public string DisplayName = "";
    public string? Model;
    public float StartHealth;
    public string RegistryName => NetName;

    public virtual void Spawn(Entity e) { }
    public virtual void Enter(Entity vehicle, Entity player) { }
    public virtual void Exit(Entity vehicle, Entity player) { }
    public virtual void Think(Entity vehicle) { }
}

// catalogs (the FOREACH targets)
public static class Monsters
{
    public static IReadOnlyList<Monster> All => Registry<Monster>.All;
    public static int Count => Registry<Monster>.Count;
    public static Monster? ByName(string name) => Registry<Monster>.ByName(name);
}

public static class Turrets
{
    public static IReadOnlyList<Turret> All => Registry<Turret>.All;
    public static int Count => Registry<Turret>.Count;
    public static Turret? ByName(string name) => Registry<Turret>.ByName(name);
}

public static class Vehicles
{
    public static IReadOnlyList<Vehicle> All => Registry<Vehicle>.All;
    public static int Count => Registry<Vehicle>.Count;
    public static Vehicle? ByName(string name) => Registry<Vehicle>.ByName(name);
}

/// <summary>
/// Registry of map-entity spawn functions (QC spawnfunc_CLASSNAME). The BSP entity lump maps a
/// "classname" to one of these setup delegates. Map objects (func_door, trigger_*, …) register here.
/// </summary>
public static class SpawnFuncs
{
    private static readonly Dictionary<string, Action<Entity>> _map = new(StringComparer.OrdinalIgnoreCase);

    public static void Register(string className, Action<Entity> setup) => _map[className] = setup;

    public static bool TrySpawn(string className, Entity e)
    {
        if (_map.TryGetValue(className, out var f)) { f(e); return true; }
        return false;
    }

    public static int Count => _map.Count;
    public static IReadOnlyDictionary<string, Action<Entity>> All => _map;
}
