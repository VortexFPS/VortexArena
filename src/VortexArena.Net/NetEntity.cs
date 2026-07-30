using System.Numerics;

namespace XonoticGodot.Net;

/// <summary>
/// What kind of networked entity a <see cref="NetEntityState"/> describes — the successor to the QC
/// <c>entcs</c>/CSQCModel "which read function" dispatch. Lets the client pick the right render representation
/// (a player model + nameplate, a projectile + trail, a pickup, …) from one unified delta-compressed stream.
/// </summary>
public enum NetEntityKind : byte
{
    None = 0,
    Player = 1,      // a remote player (csqcmodel: model + animation + nameplate + radar)
    Projectile = 2,  // a flying projectile (CSQCProjectile: model/sprite + trail)
    Item = 3,        // a world pickup (static unless it bobs/rotates)
    Gib = 4,         // a gib/debris chunk
    Generic = 5,     // any other networked entity (mapobject, turret, monster, …)
    ViewModel = 6,   // a weapon view-entity owned by a player (CL_SpawnWeaponentity / wepent)
    Nameplate = 7,   // ent_cs lightweight: position+team+health only (radar/nameplate, no model)
    NadeOrb = 8,     // a nade_orb effect entity (heal/ammo/entrap/veil orb — model + color-flash containment field)
}

/// <summary>
/// Per-entity boolean flags carried in two bytes when the <see cref="EntityField.Flags"/> bit is delta'd. These
/// mirror the CSQCModel property flags that affect interpolation/rendering rather than a transform value.
/// (Widened from a single byte to a <see cref="ushort"/> when <see cref="UsingJetpack"/> = bit 8 was added — the
/// original 8-bit range was full; <see cref="EntityStateCodec"/> writes/reads the field as a 16-bit value.)
/// </summary>
[Flags]
public enum NetEntityFlags : ushort
{
    None = 0,
    Teleported = 1 << 0, // CSQCMODEL_PROPERTY_TELEPORTED / IFLAG_TELEPORTED — cancel interpolation this update
    OnGround = 1 << 1,   // standing on the ground (affects remote anim / step smoothing)
    Crouched = 1 << 2,   // ducking (smaller hull / crouch anim)
    Dead = 1 << 3,       // a corpse (no nameplate, ragdoll anim)
    ItemExpiring = 1 << 4, // QC ITS_EXPIRING — a loot item in its despawn-fx window (client fades + emits puffs)
    ItemAnimate1 = 1 << 5, // QC ITS_ANIMATE1 — powerup/weapon pickup: client spins yaw +180°/s and bobs 10 + 8·sin(2t)
    ItemAnimate2 = 1 << 6, // QC ITS_ANIMATE2 — health/armor pickup: client spins yaw -90°/s and bobs 8 + 4·sin(3t)
    ItemGhost = 1 << 7,    // QC !ITS_AVAILABLE — a picked-up item awaiting respawn: client renders it faded (cl_ghost_items)
    // QC `ITEMS_STAT(this) & IT_USING_JETPACK` (common/physics/player.qc:878): the player's jetpack is firing this
    // tick. CSQC derives csqcmodel_modelflags |= MF_ROCKET from this networked items bit (player.qc:879), which
    // drives the rocket trail AND the looping jetpack-fly sound (csqcmodel_hooks.qc:611/645). The port networks it
    // here so the client can re-derive MF_ROCKET per player (ComposeForcedAppearance) instead of networking MF_*.
    UsingJetpack = 1 << 8,
}

/// <summary>
/// Which fields of a <see cref="NetEntityState"/> are present in a delta — the C# successor to QC's
/// <c>SendFlags</c>/<c>ReadFlags</c> change mask (csqcmodel <c>ALLPROPERTIES</c>). A 32-bit mask leads each
/// entity's delta; only the set fields follow on the wire, so an idle entity (mask 0) costs just its id.
/// (Widened from 16-bit to 32-bit when <see cref="StatusEffects"/> was added — bit 16 overflowed the old
/// <c>ushort</c>; <see cref="EntityStateCodec.WriteDelta"/>/<see cref="EntityStateCodec.ReadDelta"/> carry the
/// mask as a 32-bit value accordingly.) Highest bit currently in use: bit 24 <see cref="VehicleView"/> (the 32-bit
/// mask still has ample headroom).
/// </summary>
[Flags]
public enum EntityField : uint
{
    None = 0,
    Kind = 1 << 0,
    ModelIndex = 1 << 1,
    Frame = 1 << 2,
    Skin = 1 << 3,
    Origin = 1 << 4,
    Angles = 1 << 5,
    Velocity = 1 << 6,
    Effects = 1 << 7,
    Colormap = 1 << 8,
    Health = 1 << 9,
    Flags = 1 << 10,
    Owner = 1 << 11,  // the player entnum this entity belongs to (view-models, nameplates, projectiles)
    Weapon = 1 << 12, // the active/held weapon registry id (renders a remote player's weapon — QC wepent)
    Model = 1 << 13,  // the model NAME (precache path); networked for players/bots so the client loads the IQM

    // [T41] client-feedback stats networked on the owning player's entity (QC owner-only STATs). These ride the
    // same delta as the rest of the owner's state; they are 0 on every non-owner entity (so they cost nothing on
    // the wire for remote players / projectiles, where the mask bit stays clear).
    Feedback = 1 << 14, // the HitsoundDamageDealtTotal + objective-ring fractions (NadeTimer/Capture/Revive)

    // [T68] the QC entcs ARMOR slice (ent_cs.qc ENTCS_PROP_RESOURCE(ARMOR, …)). Networked alongside Health for the
    // shownames teammate status bar (Draw_ShowNames reads RES_ARMOR off the entcs entity for same-team players).
    // 0 on non-player entities / when armor is unchanged, so it costs nothing on the wire when the bit stays clear.
    Armor = 1 << 15, // a player's armor value (the entcs private ARMOR slice — shownames teammate status bar)

    // [A5 #3/#7] the QC ENT_CLIENT_STATUSEFFECTS channel (status_effects.qh StatusEffects_Write/Send). Carries an
    // entity's full status-effect bitmap (frozen/burning/buffs/powerups — the grouped major/minor + per-effect
    // time+flags blob produced by StatusEffectsCatalog.Write) so the client drives the remote burning/frozen
    // overlays. Bit 16 — this is the field that pushed the mask past 16 bits and widened EntityField to uint. The
    // blob is null/empty on every entity with no networked effects, so the bit stays clear and costs nothing.
    StatusEffects = 1 << 16,

    // [W1-alpha-net] the QC csqcmodel m_alpha / .alpha render-transparency channel (csqcmodel_settings.qh
    // CSQCMODEL_PROPERTY_ALPHA). Carries a per-entity render alpha so the client fades a transparent entity —
    // the Cloaked mutator (default_player_alpha 0.25), Running Guns (invisible player / visible gun), the
    // Invisibility powerup, and any death/spawn fade. Sent as a byte (0..255 = 0..1) only when it changes; the
    // default (fully opaque) never sets the bit, so an opaque entity costs nothing on the wire.
    Alpha = 1 << 17,

    // [W14a-anim] the QC csqcmodel upper-body ACTION channel (the animdecide getupperanim result + its start time —
    // SHOOT/MELEE/PAIN/DRAW/TAUNT/DEAD). Bit 18, verified free. Carries a one-byte action id + the action's start
    // time (a raw float, the same client-observed-time intended divergence as csqcmodel death_time). The producer
    // (server animdecide, LI1) is a later wave; this wave RESERVES the wire so Wave-15 is unblocked — the fields stay
    // at their defaults (UpperAction 0 = idle), so the bit stays clear and costs nothing until a producer sets them.
    AnimAction = 1 << 18,

    // [W14a-wepent] the QC wepent exterior-weapon block (common/wepent.qh: m_switchweapon / m_switchingweapon /
    // .state raise/drop / viewmodel skin / weapon alpha / gunalign). Bit 19, verified free. Lets the client render a
    // remote player's weapon switch (raise/lower), exterior-weapon transparency (Cloaked/Running-Guns gun fade), the
    // viewmodel skin, and the gun-align side. Sent only when any wepent field differs from the baseline.
    Wepent = 1 << 19,

    // [W-wepent-view] QC per-player wepent HUD view-state (vortex/oknex charge+pool, clip_load/size, hagar_load,
    // minelayer_mines, arc_heat, viewmodel frame, beam state). Reaches ALL clients (spectators, third-person),
    // unlike the owner-block OwnerWeaponRings which is local-only. Bit 20, verified free (Wepent=1<<19 is the prior
    // top bit).
    WepentView = 1 << 20,

    // [W-objstream/turret] QC turret head aim block (tur_head.angles pitch/yaw, tur_head.avelocity.y idle-spin, and
    // the .active / DEAD_DEAD flags). Present only for turret-class Generic entities whose head aim/active changed
    // vs baseline; zero/default on every other entity so the bit stays clear and costs nothing. Bit 21, verified free.
    TurretHead = 1 << 21,

    // [W-objstream/obj] QC objective entity STATUS block (generator/CP-pad/build-icon): the GtObjHealth/GtObjMaxHealth
    // fraction as an hp/255 byte plus a build-state enum (0 neutral, 1 building, 2 built/captured, 3 destroyed —
    // the cpicon build-bar vs generator healthbar selector). Present only for objective entities whose health
    // fraction or build state changed. Bit 22, verified free.
    ObjState = 1 << 22,

    // [W-nadeclient] QC nade_orb effect-entity STATE block (Nades registry id + expire time + radius). Present ONLY
    // on a nade_orb entity (NetEntityKind.NadeOrb): selects the orb model/flash color, drives the spawn..expire
    // scale-up/fade phase, and the in-orb color-flash containment radius. 0/default on every other entity so the bit
    // stays clear and costs nothing. Bit 23, verified free (ObjState=1<<22 was the prior top bit).
    NadeOrbState = 1 << 23,

    // [W-vehicleview] QC per-player vehicle HUD view-state (health/shield/energy/ammo/reload bars + vehicle hud id +
    // weapon-2 mode + lock strength/flags). Reaches all clients incl. the local pilot via LocalState; an on-foot /
    // observing player keeps the bit clear (VehKind 0 == VehicleViewState.None) so it costs nothing. Bit 24, verified
    // free. (NOTE: the design assumed bit 23, but [W-nadeclient] NadeOrbState landed in this same file at bit 23 this
    // phase, so VehicleView took the next free bit 24 — both producers/consumers parse via the named EntityField bit,
    // not a hardcoded ordinal, so the shift is transparent.)
    VehicleView = 1 << 24,

    // [r15 #43] the QC entcs clientcolors slice (ent_cs.qc ENTCS_PROP(COLORS) → .clientcolors): a player's packed
    // 16*shirt+pants palette colors — the FFA profile color (_cl_color) or the team-forced 17*teamcode. Drives the
    // _shirt/_pants mask tint + glowmod on the player model AND their weapon models (Base: viewmodel colormap =
    // 256+c in view.qc:317, exterior gun colormap = owner's in weaponsystem.qc:180). Distinct from Colormap, which
    // this port's wire uses as the TEAM byte (shownames/radar compares) — repurposing it would break team logic.
    // 0 = colorless (bit stays clear, costs nothing; client falls back to the team path). Bit 25, verified free.
    Colors = 1 << 25,
}

/// <summary>
/// The networked state of one entity — the unified "property table" the server replicates and the client
/// renders/interpolates. This single value type backs BOTH the snapshot delta-compression (the
/// <see cref="EntityStateCodec"/> writes only changed fields against a baseline) and the CSQC networked-entity
/// model (the field set is csqcmodel <c>ALLPROPERTIES</c> plus the <c>entcs</c> radar slice). Origin/Velocity
/// ride full 32-bit floats (protocol v15 — DP7's coord path; the old 13i fixed point wrapped at ±4096 qu,
/// the r16 invisible-blue-side bug on implosion); angles keep the 8i path (mod-360 wrap is safe).
/// </summary>
public struct NetEntityState
{
    /// <summary>Network entity id (QC <c>entnum</c>) — the dictionary key; written at the snapshot level, not in the delta.</summary>
    public int EntNum;

    public NetEntityKind Kind;
    public int ModelIndex;
    public int Frame;
    public int Skin;
    public Vector3 Origin;
    public Vector3 Angles;
    public Vector3 Velocity;
    public int Effects;          // EF_* render flags bitfield
    public int Colormap;         // player colors (top/bottom) or team tint
    public int Colors;           // [r15 #43] packed 16*shirt+pants clientcolors (0 = colorless, bit stays clear)
    public int Health;           // for nameplates / the owner HUD (0 when not applicable)
    public int Armor;            // [T68] QC entcs RES_ARMOR slice — the shownames teammate status bar (0 when N/A)

    /// <summary>
    /// [W1-alpha-net] QC csqcmodel <c>m_alpha</c> / <c>.alpha</c> render transparency, quantized to a byte
    /// (1..254 = 1/255..254/255; <b>0 = the default "fully opaque"</b>, which is NOT networked — the
    /// <see cref="EntityField.Alpha"/> bit stays clear so an opaque entity costs nothing on the wire and a
    /// never-seen entity (Empty baseline, all zero) reads as opaque; <b>255 = the QC <c>-1</c> "do not render"
    /// sentinel</b>, a DISTINCT hidden marker — Running Guns hides the player model while the gun stays visible).
    /// Set by the producer only when an entity's alpha drops below 1 (Cloaked/Running Guns/Invisibility/death-fade).
    /// The client maps 0 → opaque, 255 → hidden (-1), else <c>Alpha/255</c>.
    /// </summary>
    public int Alpha;
    public NetEntityFlags Flags;
    public int Owner;            // owning player's entnum (view-models / nameplates / projectiles); 0 = none
    public int Weapon;           // active/held weapon registry id (−1 = none) — renders a remote player's weapon
    public string Model;         // model name / precache path (QC .model) — the client loads the mesh by name

    // [T41] client-feedback stats (QC STAT(HITSOUND_DAMAGE_DEALT_TOTAL) + the HUD_Draw objective rings). Only
    // meaningful on the LOCAL player's own entity; 0 everywhere else. Carried together under EntityField.Feedback.
    /// <summary>QC STAT(HITSOUND_DAMAGE_DEALT_TOTAL): cumulative damage the owner has dealt; the client diffs it
    /// against the previous update to drive the hit-confirmation sound (view.qc UpdateDamage/HitSound).</summary>
    public float HitDamageDealtTotal;
    /// <summary>QC STAT(NADE_TIMER): 0..1 held-nade charge — the top-priority HUD objective ring (view.qc:1006).</summary>
    public float NadeTimer;
    /// <summary>QC STAT(CAPTURE_PROGRESS): 0..1 objective-capture progress — the 2nd-priority ring (view.qc:1012).</summary>
    public float CaptureProgress;
    /// <summary>QC STAT(REVIVE_PROGRESS): 0..1 freeze-tag thaw progress — the 3rd-priority ring (view.qc:1017).</summary>
    public float ReviveProgress;

    // [W-nadeclient] QC owner-only nade STATs, carried in the SAME EntityField.Feedback block (appended after the four
    // ring floats). Only meaningful on the LOCAL player's own entity; 0 everywhere else so the Feedback bit stays clear
    // for remotes/projectiles.
    /// <summary>QC STAT(NADE_DARKNESS_TIME): absolute server time the darkness-nade blind expires; the client computes
    /// remaining = NadeDarknessTime − now to fade the darkness overlay.</summary>
    public float NadeDarknessTime;
    /// <summary>QC STAT(NADE_BONUS): banked nade-bonus count (0..3) — the HUD ammo-panel nade-bonus counter.</summary>
    public int NadeBonus;
    /// <summary>QC STAT(NADE_BONUS_TYPE): the banked nade's Nades registry id (0..11) — selects the bonus icon/color.</summary>
    public int NadeBonusType;
    /// <summary>QC STAT(NADE_BONUS_SCORE): 0..1 fraction toward the next nade bonus — the bonus progress bar.</summary>
    public float NadeBonusScore;

    /// <summary>
    /// [A5 #3/#7] QC <c>ENT_CLIENT_STATUSEFFECTS</c> blob — the entity's full status-effect bitmap as produced by
    /// <c>StatusEffectsCatalog.Write</c> (the grouped major/minor mask + per-effect time+flags). <c>null</c> (or an
    /// empty array) means "no networked effects": the <see cref="EntityField.StatusEffects"/> bit stays clear so it
    /// costs nothing on the wire. The full bitmap is re-sent only on the tick it changes (the delta compares it by
    /// content); the client decodes it with <c>StatusEffectsCatalog.Read</c> to drive the burning/frozen overlays.
    /// </summary>
    public byte[]? StatusEffects;

    // [W14a-anim] QC csqcmodel upper-body action overlay (the animdecide getupperanim result). RESERVED this wave —
    // the server producer (LI1) lands later; defaults (UpperAction 0 = idle) keep the EntityField.AnimAction bit clear.
    /// <summary>QC animdecide upper-body action id (0 = idle/no-overlay; SHOOT/MELEE/PAIN1/PAIN2/DRAW/TAUNT/DEAD).
    /// The client plays this as a torso overlay on top of the velocity-derived legs (LI3).</summary>
    public byte UpperAction;
    /// <summary>QC the action's start time (server time the upper-body action began) — a raw float, the same
    /// client-observed-time intended divergence as the csqcmodel death_time channel; the client derives the action's
    /// play phase as <c>now − AnimActionTime</c>.</summary>
    public float AnimActionTime;

    // [W14a-wepent] QC wepent exterior-weapon block (common/wepent.qh) — the remote third-person held weapon's
    // switch/transparency/skin state. Defaults (-1 switch ids, 0 phase/skin/alpha/align) keep the EntityField.Wepent
    // bit clear so a player not mid-switch with an opaque opaque-skin gun costs nothing on the wire.
    /// <summary>QC <c>.m_switchweapon</c> — the weapon RegistryId the slot is switching TO (-1 = none/keep).</summary>
    public int SwitchWeapon;
    /// <summary>QC <c>.m_switchingweapon</c> — the in-transition weapon being switched-to mid raise/drop (-1 = none).</summary>
    public int SwitchingWeapon;
    /// <summary>QC the slot's <c>.state</c> compressed to the render-relevant phase: 0 = ready/idle, 1 = WS_RAISE
    /// (raising in), 2 = WS_DROP (lowering out). Drives the remote weapon raise/lower tween (QW5).</summary>
    public byte WepPhase;
    /// <summary>QC the exterior weapon's <c>.skin</c> (viewmodel skin variant) applied to the built held model.</summary>
    public byte ViewmodelSkin;
    /// <summary>QC the exterior weapon's <c>.alpha</c>, quantized exactly like the body <see cref="Alpha"/> (0 = opaque
    /// and not networked; 1..254 = alpha/255; 255 = the QC <c>-1</c> hidden sentinel — Running Guns hides the player
    /// but keeps the gun visible, so the gun's alpha is networked independently).</summary>
    public byte WepAlpha;
    /// <summary>QC the gun-align side (cl_gunalign / w_gunalign) — which hand/side the exterior weapon sits on.</summary>
    public byte GunAlign;

    /// <summary>
    /// [W-wepent-view] QC per-player wepent HUD view-state block — the vortex/oknex charge+pool, clip load/size,
    /// hagar load, minelayer mine count, arc heat, viewmodel anim frame, and beam state for the third-person /
    /// spectated player's weapon rings (decoded via <see cref="WepentViewState"/>). All-default (no charge, no clip)
    /// keeps the <see cref="EntityField.WepentView"/> bit clear so an idle player costs nothing on the wire.
    /// </summary>
    public WepentViewState WepentView;

    // [W-objstream] QC turret head aim + objective STATUS blocks. Both default to 0 on non-turret/non-objective
    // entities, so NetEntityState.Diff leaves the TurretHead / ObjState bits clear and a normal player/projectile/item
    // costs nothing for these groups.
    /// <summary>QC <c>tur_head.angles.x</c> — body-relative head pitch (deg).</summary>
    public float TurHeadPitch;
    /// <summary>QC <c>tur_head.angles.y</c> — body-relative head yaw (deg).</summary>
    public float TurHeadYaw;
    /// <summary>QC <c>tur_head.avelocity.y</c> — head yaw angular velocity (deg/s) the client integrates for the
    /// idle head spin (FusionReactor/Tesla; cl_turrets turret_draw <c>tur_head.angles += avelocity*dt</c>).</summary>
    public float TurHeadAVelYaw;
    /// <summary>QC turret render flags: bit0 = Active (.active != 0), bit1 = Dead (DEAD_DEAD).</summary>
    public byte TurFlags;
    /// <summary>QC generator/cpicon STATUS byte: clamp(round(GtObjHealth/GtObjMaxHealth*255),0,255); 0 = destroyed,
    /// 255 = full. Client recovers fraction = ObjHealthByte/255.</summary>
    public byte ObjHealthByte;
    /// <summary>QC objective state: 0 = neutral/idle, 1 = building, 2 = built/captured/owned, 3 = destroyed
    /// (the cpicon build-bar vs generator healthbar selector).</summary>
    public byte ObjState;

    // [W-nadeclient] QC nade_orb STATE block — present ONLY on a NetEntityKind.NadeOrb entity (carried under
    // EntityField.NadeOrbState). 0/default on every other entity so the bit stays clear. The orb's Origin rides the
    // normal Origin field.
    /// <summary>QC orb Nades registry id (1..11) — selects the orb model + flash color.</summary>
    public byte OrbType;
    /// <summary>QC absolute server time the orb expires; the client derives the spawn..expire scale-up/fade phase.</summary>
    public float OrbExpire;
    /// <summary>QC orb radius in qu (250..500) — the in-orb color-flash containment test + 3D model scale.</summary>
    public int OrbRadius;

    /// <summary>
    /// [W-vehicleview] QC per-player vehicle HUD view-state block — the health/shield/energy/ammo/reload bars, the
    /// vehicle hud id, the weapon-2 mode, and the lock strength/flags for the seated-pilot HUD and any remote/spectated
    /// view (decoded via <see cref="VehicleViewState"/>). <see cref="VehicleViewState.None"/> (VehKind 0 = on-foot /
    /// observing) keeps the <see cref="EntityField.VehicleView"/> bit clear so a non-pilot costs nothing on the wire.
    /// </summary>
    public VehicleViewState VehicleView;

    /// <summary>A fresh state carrying just an id (the implicit baseline for a never-seen entity — a "spawn").</summary>
    public static NetEntityState Empty(int entNum) => new() { EntNum = entNum, SwitchWeapon = -1, SwitchingWeapon = -1, VehicleView = VehicleViewState.None };

    /// <summary>The set of fields that differ between <paramref name="baseline"/> and <paramref name="current"/> (the SendFlags).</summary>
    public static EntityField Diff(in NetEntityState baseline, in NetEntityState current)
    {
        EntityField m = EntityField.None;
        if (baseline.Kind != current.Kind) m |= EntityField.Kind;
        if (baseline.ModelIndex != current.ModelIndex) m |= EntityField.ModelIndex;
        if (baseline.Frame != current.Frame) m |= EntityField.Frame;
        if (baseline.Skin != current.Skin) m |= EntityField.Skin;
        if (baseline.Origin != current.Origin) m |= EntityField.Origin;
        if (baseline.Angles != current.Angles) m |= EntityField.Angles;
        if (baseline.Velocity != current.Velocity) m |= EntityField.Velocity;
        if (baseline.Effects != current.Effects) m |= EntityField.Effects;
        if (baseline.Colormap != current.Colormap) m |= EntityField.Colormap;
        if (baseline.Colors != current.Colors) m |= EntityField.Colors;
        if (baseline.Health != current.Health) m |= EntityField.Health;
        if (baseline.Armor != current.Armor) m |= EntityField.Armor;
        if (baseline.Alpha != current.Alpha) m |= EntityField.Alpha;
        if (baseline.Flags != current.Flags) m |= EntityField.Flags;
        if (baseline.Owner != current.Owner) m |= EntityField.Owner;
        if (baseline.Weapon != current.Weapon) m |= EntityField.Weapon;
        if (baseline.Model != current.Model) m |= EntityField.Model;
        if (baseline.HitDamageDealtTotal != current.HitDamageDealtTotal
            || baseline.NadeTimer != current.NadeTimer
            || baseline.CaptureProgress != current.CaptureProgress
            || baseline.ReviveProgress != current.ReviveProgress
            || baseline.NadeDarknessTime != current.NadeDarknessTime
            || baseline.NadeBonus != current.NadeBonus
            || baseline.NadeBonusType != current.NadeBonusType
            || baseline.NadeBonusScore != current.NadeBonusScore) m |= EntityField.Feedback;
        // The status-effect blob is a byte[]; compare by CONTENT (not reference) so the full bitmap is re-sent only
        // when it actually changes. null and empty both mean "no effects" and compare equal.
        if (!StatusBlobEqual(baseline.StatusEffects, current.StatusEffects)) m |= EntityField.StatusEffects;
        // [W14a-anim] the upper-body action overlay — re-send when the action id OR its start time changes (a new
        // SHOOT/PAIN/… latch, or a re-latch of the same action). Both default 0 → the bit stays clear when idle.
        if (baseline.UpperAction != current.UpperAction
            || baseline.AnimActionTime != current.AnimActionTime) m |= EntityField.AnimAction;
        // [W14a-wepent] the exterior-weapon block — re-send when ANY wepent field differs (a switch start/finish, a
        // raise/drop phase change, the gun alpha/skin/align changing). All-default (-1/-1/0/0/0/0) keeps it clear.
        if (baseline.SwitchWeapon != current.SwitchWeapon
            || baseline.SwitchingWeapon != current.SwitchingWeapon
            || baseline.WepPhase != current.WepPhase
            || baseline.ViewmodelSkin != current.ViewmodelSkin
            || baseline.WepAlpha != current.WepAlpha
            || baseline.GunAlign != current.GunAlign) m |= EntityField.Wepent;
        // [W-wepent-view] the per-player HUD view-state block — re-send when ANY field differs (charge/pool/clip/heat/
        // frame/beam). All-default (no charge, no clip) keeps the bit clear.
        if (!baseline.WepentView.Equals(current.WepentView)) m |= EntityField.WepentView;
        // [W-objstream] turret head aim — re-send when head pitch/yaw, head yaw avel, or the active/dead flags change.
        if (baseline.TurHeadPitch != current.TurHeadPitch
            || baseline.TurHeadYaw != current.TurHeadYaw
            || baseline.TurHeadAVelYaw != current.TurHeadAVelYaw
            || baseline.TurFlags != current.TurFlags) m |= EntityField.TurretHead;
        // [W-objstream] objective STATUS — re-send when the obj-health fraction byte or the build state changes.
        if (baseline.ObjHealthByte != current.ObjHealthByte
            || baseline.ObjState != current.ObjState) m |= EntityField.ObjState;
        // [W-nadeclient] nade_orb STATE — re-send when the orb type, expire time, or radius differ. Default (0/0/0)
        // keeps the bit clear on every non-orb entity.
        if (baseline.OrbType != current.OrbType
            || baseline.OrbExpire != current.OrbExpire
            || baseline.OrbRadius != current.OrbRadius) m |= EntityField.NadeOrbState;
        // [W-vehicleview] per-player vehicle HUD view-state — re-send when ANY field differs. None (VehKind 0 =
        // on-foot/observing) keeps the bit clear so a non-pilot costs nothing.
        if (!baseline.VehicleView.Equals(current.VehicleView)) m |= EntityField.VehicleView;
        return m;
    }

    /// <summary>Content equality for the status-effect blob, treating <c>null</c> and an empty array as equal.</summary>
    internal static bool StatusBlobEqual(byte[]? a, byte[]? b)
    {
        int la = a is null ? 0 : a.Length;
        int lb = b is null ? 0 : b.Length;
        if (la != lb) return false;
        for (int i = 0; i < la; i++)
            if (a![i] != b![i]) return false;
        return true;
    }
}

/// <summary>
/// Serializes a <see cref="NetEntityState"/> as a delta against a baseline — the change-mask codec that
/// powers both snapshot delta-compression and CSQC entity updates (QC <c>SendEntity</c>/<c>ReadEntity</c> with
/// <c>SendFlags</c>). <see cref="WriteDelta"/> emits a 32-bit <see cref="EntityField"/> mask followed by only
/// the changed fields; <see cref="ReadDelta"/> applies them on top of the baseline. A spawn is just a delta
/// against <see cref="NetEntityState.Empty"/>; an idle entity is a single zero mask. The append-only tail beyond
/// the original 16-bit set is, in wire order: StatusEffects(16), Alpha(17), AnimAction(18), Wepent(19),
/// WepentView(20), TurretHead(21), ObjState(22), NadeOrbState(23), VehicleView(24) — every new field MUST be
/// appended AFTER the existing blocks in both WriteDelta and ReadDelta or every later field desyncs. (The
/// NadeDarknessTime/NadeBonus/NadeBonusType/NadeBonusScore nade owner-stats are appended INSIDE the Feedback(14)
/// block after ReviveProgress, in lockstep on both sides.)
/// </summary>
public static class EntityStateCodec
{
    /// <summary>Write <paramref name="current"/> as the changed-fields delta from <paramref name="baseline"/>. Returns the mask written.</summary>
    public static EntityField WriteDelta(BitWriter w, in NetEntityState baseline, in NetEntityState current)
    {
        EntityField mask = NetEntityState.Diff(baseline, current);
        // 32-bit mask (widened from ushort when EntityField.StatusEffects = bit 16 was added).
        w.WriteULong((uint)mask);

        if ((mask & EntityField.Kind) != 0) w.WriteByte((byte)current.Kind);
        if ((mask & EntityField.ModelIndex) != 0) w.WriteUShort(current.ModelIndex);
        if ((mask & EntityField.Frame) != 0) w.WriteUShort(current.Frame);
        if ((mask & EntityField.Skin) != 0) w.WriteByte(current.Skin & 0xFF);
        // Origin/Velocity ride FULL floats (protocol v15) — DP7-faithful, matching the owner block. The old
        // NetPrecision.Low (13-bit fixed point, EncodeCoord13's unchecked (short) cast) WRAPS at ±4096 qu:
        // implosion's blue half lives past +4096 on X and −4096 on Y, so every entity there decoded into the
        // void (invisible projectiles/trails/items on the whole blue side — r16). Velocity wraps at ±4096
        // qu/s, which blaster-class bolt speeds (6000) exceed on EVERY map, sign-flipping the client's
        // between-snapshot extrapolation. Angles stay Low — mod-360 wrapping is safe by construction.
        if ((mask & EntityField.Origin) != 0) w.WriteVector(current.Origin, NetPrecision.Float);
        if ((mask & EntityField.Angles) != 0) w.WriteAngles(current.Angles, NetPrecision.Low);
        if ((mask & EntityField.Velocity) != 0) w.WriteVector(current.Velocity, NetPrecision.Float);
        if ((mask & EntityField.Effects) != 0) w.WriteLong(current.Effects);
        if ((mask & EntityField.Colormap) != 0) w.WriteByte(current.Colormap & 0xFF);
        if ((mask & EntityField.Colors) != 0) w.WriteByte(current.Colors & 0xFF);
        if ((mask & EntityField.Health) != 0) w.WriteShort(current.Health);
        if ((mask & EntityField.Armor) != 0) w.WriteShort(current.Armor);
        if ((mask & EntityField.Alpha) != 0) w.WriteByte(current.Alpha & 0xFF); // 0 = opaque; 1..254 = alpha/255; 255 = hidden (-1)
        if ((mask & EntityField.Flags) != 0) w.WriteUShort((ushort)current.Flags); // 16-bit since UsingJetpack=bit 8 widened it
        if ((mask & EntityField.Owner) != 0) w.WriteUShort(current.Owner);
        if ((mask & EntityField.Weapon) != 0) w.WriteShort(current.Weapon);
        if ((mask & EntityField.Model) != 0) w.WriteString(current.Model);
        if ((mask & EntityField.Feedback) != 0)
        {
            w.WriteFloat(current.HitDamageDealtTotal);
            w.WriteFloat(current.NadeTimer);
            w.WriteFloat(current.CaptureProgress);
            w.WriteFloat(current.ReviveProgress);
            // [W-nadeclient] owner-only nade STATs appended to the Feedback block (after ReviveProgress).
            w.WriteFloat(current.NadeDarknessTime);
            w.WriteUShort(current.NadeBonus);
            w.WriteByte((byte)current.NadeBonusType);
            w.WriteFloat(current.NadeBonusScore);
        }
        if ((mask & EntityField.StatusEffects) != 0)
        {
            // Length-prefixed blob (the SessionAuth pattern). A 0 length is a valid value — it means "effects just
            // cleared", so the client reads an empty bitmap and drops the entity's effects.
            byte[]? blob = current.StatusEffects;
            int n = blob is null ? 0 : blob.Length;
            w.WriteUShort(n);
            if (n > 0) w.WriteBytes(blob);
        }
        // [W14a-anim] upper-body action: a byte action id + the raw float start time (the death_time-class divergence).
        if ((mask & EntityField.AnimAction) != 0)
        {
            w.WriteByte(current.UpperAction);
            w.WriteFloat(current.AnimActionTime);
        }
        // [W14a-wepent] exterior-weapon block: switch/switching as signed shorts (-1 = none), then the four bytes
        // phase/skin/alpha/align. (Porto held-angle + tuba note are RESERVED — deferred to a later wave; when added
        // they MUST be appended AFTER GunAlign and read in the same order, or every later field desyncs.)
        if ((mask & EntityField.Wepent) != 0)
        {
            w.WriteShort(current.SwitchWeapon);
            w.WriteShort(current.SwitchingWeapon);
            w.WriteByte(current.WepPhase);
            w.WriteByte(current.ViewmodelSkin);
            w.WriteByte(current.WepAlpha); // 0 = opaque; 1..254 = alpha/255; 255 = hidden (-1)
            w.WriteByte(current.GunAlign);
        }
        // [W-wepent-view] per-player HUD view-state block (fixed 13-byte layout, see WepentViewCodec.Write).
        if ((mask & EntityField.WepentView) != 0) WepentViewCodec.Write(w, current.WepentView);
        // [W-objstream] turret head aim: head angles (Low, roll always 0), the yaw avel float, then the flags byte.
        if ((mask & EntityField.TurretHead) != 0)
        {
            w.WriteAngles(new System.Numerics.Vector3(current.TurHeadPitch, current.TurHeadYaw, 0f), NetPrecision.Low);
            w.WriteFloat(current.TurHeadAVelYaw);
            w.WriteByte(current.TurFlags);
        }
        // [W-objstream] objective STATUS: the hp/255 fraction byte, then the build-state enum byte.
        if ((mask & EntityField.ObjState) != 0)
        {
            w.WriteByte(current.ObjHealthByte);
            w.WriteByte(current.ObjState);
        }
        // [W-nadeclient] nade_orb STATE block (orb id byte, expire float, radius ushort) — APPENDED after ObjState.
        if ((mask & EntityField.NadeOrbState) != 0)
        {
            w.WriteByte(current.OrbType);
            w.WriteFloat(current.OrbExpire);
            w.WriteUShort(current.OrbRadius);
        }
        // [W-vehicleview] per-player vehicle HUD view-state — the FINAL block (fixed 11-byte VehicleViewCodec layout).
        if ((mask & EntityField.VehicleView) != 0) VehicleViewCodec.Write(w, current.VehicleView);
        return mask;
    }

    /// <summary>Read a delta and apply it onto <paramref name="baseline"/>, returning the reconstructed state.</summary>
    public static NetEntityState ReadDelta(ref BitReader r, in NetEntityState baseline)
    {
        NetEntityState s = baseline;
        var mask = (EntityField)r.ReadULong();

        if ((mask & EntityField.Kind) != 0) s.Kind = (NetEntityKind)r.ReadByte();
        if ((mask & EntityField.ModelIndex) != 0) s.ModelIndex = r.ReadUShort();
        if ((mask & EntityField.Frame) != 0) s.Frame = r.ReadUShort();
        if ((mask & EntityField.Skin) != 0) s.Skin = r.ReadByte();
        if ((mask & EntityField.Origin) != 0) s.Origin = r.ReadVector(NetPrecision.Float);   // v15: full floats (±4096 wrap fix)
        if ((mask & EntityField.Angles) != 0) s.Angles = r.ReadAngles(NetPrecision.Low);
        if ((mask & EntityField.Velocity) != 0) s.Velocity = r.ReadVector(NetPrecision.Float); // v15: full floats
        if ((mask & EntityField.Effects) != 0) s.Effects = r.ReadLong();
        if ((mask & EntityField.Colormap) != 0) s.Colormap = r.ReadByte();
        if ((mask & EntityField.Colors) != 0) s.Colors = r.ReadByte();
        if ((mask & EntityField.Health) != 0) s.Health = r.ReadShort();
        if ((mask & EntityField.Armor) != 0) s.Armor = r.ReadShort();
        if ((mask & EntityField.Alpha) != 0) s.Alpha = r.ReadByte();
        if ((mask & EntityField.Flags) != 0) s.Flags = (NetEntityFlags)r.ReadUShort();
        if ((mask & EntityField.Owner) != 0) s.Owner = r.ReadUShort();
        if ((mask & EntityField.Weapon) != 0) s.Weapon = r.ReadShort();
        if ((mask & EntityField.Model) != 0) s.Model = r.ReadString();
        if ((mask & EntityField.Feedback) != 0)
        {
            s.HitDamageDealtTotal = r.ReadFloat();
            s.NadeTimer = r.ReadFloat();
            s.CaptureProgress = r.ReadFloat();
            s.ReviveProgress = r.ReadFloat();
            // [W-nadeclient] owner-only nade STATs — SAME order as WriteDelta (after ReviveProgress).
            s.NadeDarknessTime = r.ReadFloat();
            s.NadeBonus = r.ReadUShort();
            s.NadeBonusType = r.ReadByte();
            s.NadeBonusScore = r.ReadFloat();
        }
        if ((mask & EntityField.StatusEffects) != 0)
        {
            int n = r.ReadUShort();
            // n == 0 (effects cleared) decodes to an empty array so the client distinguishes "field present, now
            // empty" (clear the list) from "field absent" (keep the baseline's blob).
            s.StatusEffects = n > 0 ? r.ReadBytes(n).ToArray() : System.Array.Empty<byte>();
        }
        // [W14a-anim] upper-body action (byte id + float start time).
        if ((mask & EntityField.AnimAction) != 0)
        {
            s.UpperAction = (byte)r.ReadByte();
            s.AnimActionTime = r.ReadFloat();
        }
        // [W14a-wepent] exterior-weapon block — SAME order as WriteDelta (switch/switching shorts, then the four bytes).
        if ((mask & EntityField.Wepent) != 0)
        {
            s.SwitchWeapon = r.ReadShort();
            s.SwitchingWeapon = r.ReadShort();
            s.WepPhase = (byte)r.ReadByte();
            s.ViewmodelSkin = (byte)r.ReadByte();
            s.WepAlpha = (byte)r.ReadByte();
            s.GunAlign = (byte)r.ReadByte();
        }
        // [W-wepent-view] per-player HUD view-state block — SAME order as WriteDelta (the fixed WepentViewCodec layout).
        if ((mask & EntityField.WepentView) != 0) s.WepentView = WepentViewCodec.Read(ref r);
        // [W-objstream] turret head aim — SAME order as WriteDelta (head angles Low, yaw avel float, flags byte).
        if ((mask & EntityField.TurretHead) != 0)
        {
            var ha = r.ReadAngles(NetPrecision.Low);
            s.TurHeadPitch = ha.X;
            s.TurHeadYaw = ha.Y;
            s.TurHeadAVelYaw = r.ReadFloat();
            s.TurFlags = (byte)r.ReadByte();
        }
        // [W-objstream] objective STATUS — SAME order as WriteDelta (hp/255 byte, then build-state byte).
        if ((mask & EntityField.ObjState) != 0)
        {
            s.ObjHealthByte = (byte)r.ReadByte();
            s.ObjState = (byte)r.ReadByte();
        }
        // [W-nadeclient] nade_orb STATE — SAME order as WriteDelta (orb id byte, expire float, radius ushort).
        if ((mask & EntityField.NadeOrbState) != 0)
        {
            s.OrbType = (byte)r.ReadByte();
            s.OrbExpire = r.ReadFloat();
            s.OrbRadius = r.ReadUShort();
        }
        // [W-vehicleview] per-player vehicle HUD view-state — the FINAL block (SAME fixed VehicleViewCodec layout).
        if ((mask & EntityField.VehicleView) != 0) s.VehicleView = VehicleViewCodec.Read(ref r);
        return s;
    }
}
