using System;
using Godot;
using VortexArena.Common.Framework;

namespace VortexArena.Game.Client;

/// <summary>
/// The per-projectile-type visual table — the faithful port of CSQC's <c>Projectile_Draw</c> /
/// <c>ENT_CLIENT_PROJECTILE</c> HANDLE switch (Base/.../qcsrc/client/weapons/projectile.qc). The QC code
/// keyed every networked projectile by a <c>csqcprojectile_type</c> (PROJECTILE_*) and, per type, selected a
/// <c>traileffect</c> (EFFECT_TR_*), a model <c>scale</c>, a spin (<c>avelocity</c>/the z-rotation in
/// <c>Projectile_Draw</c>), and a looping fly sound. This is that table, so the
/// <see cref="ProjectileRenderer"/> can give a rocket its smoke-and-z-spin, electro its blue plasma trail,
/// crylink its purple shards, the fireball its particle-only fire, etc. — instead of one generic trail.
///
/// Why a separate file: the trail *identity* (which EFFECT_TR_* a type uses) is gameplay data straight from
/// the QC; turning that identity into concrete Godot particle parameters (color / additive / density /
/// gravity) is presentation. Keeping the mapping here, beside the renderer, makes the QC correspondence
/// auditable (every <see cref="ProjectileType"/> below cites its <c>projectile.qc</c> HANDLE line).
/// </summary>
public static class ProjectileCatalog
{
    /// <summary>The networked CSQC projectile type (QC <c>PROJECTILE_*</c>). Only the types this port's
    /// weapons + vehicles actually spawn are enumerated; unknown classnames fall back to <see cref="Generic"/>.</summary>
    public enum ProjectileType
    {
        None = 0,
        Electro, Rocket, Crylink, CrylinkBouncing, ElectroBeam,
        Grenade, GrenadeBouncing, Mine, Blaster, ArcBolt, Hlac,
        PortoRed, PortoBlue, Hookbomb, Hagar, HagarBouncing,
        Fireball, Firemine, Tag, Flac, Seeker, MageSpike, GolemLightning,
        SpiderRocket, WakiRocket, WakiCannon, RaptorCannon, BumbleGun, BumbleBeam,
        Rpc, RocketMinstaLaser, Plasma, Generic,
        // Held-nade projectiles (cl_nades) — one per Nades registry type. The thrown nade renders on the
        // standard projectile path with the grenademodel.md3 body tinted by the per-type NadeRegistry m_color.
        NadeNormal, NadeNapalm, NadeIce, NadeTranslocate, NadeSpawn, NadeHeal,
        NadeMonster, NadeEntrap, NadeVeil, NadeAmmo, NadeDarkness,
    }

    /// <summary>The body look a projectile draws with when it has no resolved model (QC setmodel fallback).</summary>
    public enum BodyFamily
    {
        RocketMesh,   // an elongated solid body (rockets, seeker, spider/waki rockets)
        GrenadeMesh,  // a small tumbling solid (grenades, mines, tag)
        GlowSprite,   // an additive energy billboard (electro/crylink/arc/plasma/blaster bolts)
        FireSprite,   // a fire billboard, no solid model (fireball/firemine — QC modelindex=0)
    }

    /// <summary>Concrete particle parameters for a trail (the presentation of one EFFECT_TR_* identity).</summary>
    public readonly record struct TrailParams(
        Color Color, bool Additive, int Amount, float Life, float Scale, float Gravity);

    /// <summary>One projectile type's full visual descriptor (trail + body + spin + loop sound).</summary>
    public sealed class Desc
    {
        public required ProjectileType Type;
        /// <summary>The QC <c>traileffect</c> EFFECT_* name (informational / for routing through EffectSystem).</summary>
        public string TrailEffect = "";
        /// <summary>Concrete trail particles, or null for a trailless type (Blaster/HLAC/WakiCannon/Raptorbomb).</summary>
        public TrailParams? Trail;
        /// <summary>QC model <c>scale</c> (1 default; rocket 2, porto 4, hagar 0.75, flac 0.4, golem 2.5…).</summary>
        public float ModelScale = 1f;
        /// <summary>Continuous spin in degrees/sec about each local axis (QC <c>avelocity</c> / the z-rot in Draw).</summary>
        public Vector3 SpinDegPerSec = Vector3.Zero;
        /// <summary>Looping fly sound sample (QC <c>loopsound</c>, e.g. "weapons/rocket_fly"); null = silent.</summary>
        public string? LoopSound;
        /// <summary>The body look when no model resolves.</summary>
        public BodyFamily Body = BodyFamily.GlowSprite;
        /// <summary>Real model VFS path to render instead of the procedural <see cref="Body"/> mesh, when the host
        /// wired a <see cref="ProjectileRenderer.ModelFactory"/> — the QC <c>setmodel(MDL_PROJECTILE_*)</c>. e.g.
        /// rocket.md3 (its <c>RL</c> body + the additive <c>RocketThrust</c> flame cone) and grenademodel.md3.
        /// Null = always use the procedural body; the <see cref="Body"/> stays the asset-less fallback (headless
        /// tests / missing content / factory miss).</summary>
        public string? ModelPath;
        /// <summary>The bolt/light tint.</summary>
        public Color GlowColor = new(0.8f, 0.85f, 0.9f);
        /// <summary>Whether this type casts a dynamic point light (rockets/plasma/fireball).</summary>
        public bool HasLight;
    }

    // ============================================================================================
    //  Trail presets — one per EFFECT_TR_* identity used by projectile.qc
    // ============================================================================================

    // Grey exhaust smoke (TR_ROCKET): dense, non-additive, drifts up a touch. Godot emits per-TIME, not per
    // -distance like QC trailspacing — so the count/size are sized up for the rocket's ~1300u/s so the puffs
    // form a continuous trail instead of sparse dots.
    private static readonly TrailParams RocketSmoke = new(new Color(0.55f, 0.55f, 0.55f), false, 96, 0.8f, 2.6f, 10f);
    // Thinner smoke (TR_GRENADE) for grenades/mines.
    private static readonly TrailParams GrenadeSmoke = new(new Color(0.5f, 0.5f, 0.5f), false, 54, 0.7f, 1.6f, 8f);
    // Small light smoke (HAGAR_ROCKET / TR_SEEKER) for hagar/flac/seeker/vehicle rockets.
    private static readonly TrailParams SmallSmoke = new(new Color(0.5f, 0.5f, 0.5f), false, 22, 0.45f, 0.55f, 14f);
    // Blue plasma glow (TR_NEXUIZPLASMA): electro / vortex-ish bolts.
    private static readonly TrailParams BluePlasma = new(new Color(0.4f, 0.6f, 1.0f), true, 40, 0.4f, 0.7f, 0f);
    // Purple shards (TR_CRYLINKPLASMA): crylink / raptor cannon.
    private static readonly TrailParams PurplePlasma = new(new Color(0.8f, 0.4f, 1.0f), true, 30, 0.35f, 0.5f, 0f);
    // Pale energy spike (TR_WIZSPIKE): arc bolt / porto.
    private static readonly TrailParams WizSpike = new(new Color(0.55f, 0.9f, 0.8f), true, 24, 0.4f, 0.5f, 0f);
    // Red energy spike (TR_KNIGHTSPIKE): hookbomb.
    private static readonly TrailParams KnightSpike = new(new Color(1.0f, 0.45f, 0.3f), true, 24, 0.4f, 0.5f, 0f);
    // Violet spike (TR_VORESPIKE): mage spike.
    private static readonly TrailParams VoreSpike = new(new Color(0.7f, 0.4f, 1.0f), true, 26, 0.4f, 0.55f, 0f);
    // Fire (FIREBALL): big orange flame trail.
    private static readonly TrailParams FireBig = new(new Color(1.0f, 0.5f, 0.15f), true, 50, 0.5f, 1.1f, 30f);
    // Fire (FIREMINE): smaller flame.
    private static readonly TrailParams FireSmall = new(new Color(1.0f, 0.5f, 0.15f), true, 30, 0.45f, 0.7f, 20f);
    // Laser (ROCKETMINSTA_LASER): thin red streak.
    private static readonly TrailParams LaserRed = new(new Color(1.0f, 0.2f, 0.2f), true, 20, 0.3f, 0.4f, 0f);

    // Common tints.
    private static readonly Color ElectroBlue = new(0.45f, 0.65f, 1.0f);
    private static readonly Color CrylinkPurple = new(0.8f, 0.4f, 1.0f);
    private static readonly Color RocketOrange = new(1.0f, 0.55f, 0.2f);
    private static readonly Color FireOrange = new(1.0f, 0.5f, 0.15f);
    private static readonly Color BlasterYellow = new(1.0f, 0.9f, 0.4f);
    private static readonly Color GrenadeGreen = new(0.3f, 0.45f, 0.2f);

    // ============================================================================================
    //  The table — keyed by ProjectileType (mirrors the projectile.qc HANDLE switch)
    // ============================================================================================

    private static readonly System.Collections.Generic.Dictionary<ProjectileType, Desc> Table = Build();

    private static System.Collections.Generic.Dictionary<ProjectileType, Desc> Build()
    {
        var t = new System.Collections.Generic.Dictionary<ProjectileType, Desc>();
        void Add(Desc d) => t[d.Type] = d;

        // SECONDARY orb: MDL_PROJECTILE_ELECTRO = models/ebomb.mdl (all.inc:50 — MD3 content despite the
        // extension), EFFECT_TR_NEXUIZPLASMA, electro_fly loop, bounce (projectile.qc:346,405). The orb spins
        // with avelocity '7 0 11' (electro.qc:32 electro_orb_setup — pitch 7, roll 11 deg/s). Following the
        // rocket convention (QC roll → the body's nose/X axis), map roll 11 → X and pitch 7 → Y.
        Add(new Desc { Type = ProjectileType.Electro, TrailEffect = "TR_NEXUIZPLASMA", Trail = BluePlasma,
            Body = BodyFamily.GlowSprite, ModelPath = "models/ebomb.mdl", SpinDegPerSec = new Vector3(11f, 7f, 0f),
            GlowColor = ElectroBlue, HasLight = true, LoopSound = "weapons/electro_fly" });
        // PRIMARY bolt: MDL_PROJECTILE_ELECTRO_BEAM = models/elaser.mdl (all.inc:51), EFFECT_TR_NEXUIZPLASMA,
        // no fly loop (projectile.qc:350).
        Add(new Desc { Type = ProjectileType.ElectroBeam, TrailEffect = "TR_NEXUIZPLASMA", Trail = BluePlasma,
            Body = BodyFamily.GlowSprite, ModelPath = "models/elaser.mdl",
            GlowColor = ElectroBlue, HasLight = true });
        // EFFECT_TR_ROCKET, MDL_PROJECTILE_ROCKET, scale 2, roll 720 about the nose, devastator_fly loop
        // (projectile.qc:347,138-140,415). QC rot '0 0 720' = roll about forward; the nose is the body's local
        // +X (see OrientToVelocity), so the spin is on X, not Z.
        Add(new Desc { Type = ProjectileType.Rocket, TrailEffect = "TR_ROCKET", Trail = RocketSmoke,
            Body = BodyFamily.RocketMesh, ModelPath = "models/rocket.md3", ModelScale = 2f, SpinDegPerSec = new Vector3(720f, 0, 0),
            GlowColor = RocketOrange, HasLight = true, LoopSound = "weapons/rocket_fly" });
        // EFFECT_TR_ROCKET (RPC) — devastator fly
        Add(new Desc { Type = ProjectileType.Rpc, TrailEffect = "TR_ROCKET", Trail = RocketSmoke,
            Body = BodyFamily.RocketMesh, ModelPath = "models/rocket.md3", ModelScale = 2f, SpinDegPerSec = new Vector3(720f, 0, 0),
            GlowColor = RocketOrange, HasLight = true, LoopSound = "weapons/rocket_fly" });
        // EFFECT_TR_CRYLINKPLASMA (projectile.qc:348-349)
        Add(new Desc { Type = ProjectileType.Crylink, TrailEffect = "TR_CRYLINKPLASMA", Trail = PurplePlasma,
            Body = BodyFamily.GlowSprite, GlowColor = CrylinkPurple, HasLight = true });
        Add(new Desc { Type = ProjectileType.CrylinkBouncing, TrailEffect = "TR_CRYLINKPLASMA", Trail = PurplePlasma,
            Body = BodyFamily.GlowSprite, GlowColor = CrylinkPurple, HasLight = true });
        // EFFECT_TR_GRENADE, MDL_PROJECTILE_GRENADE, sideways tumble for bouncing (projectile.qc:351-353,132-134)
        Add(new Desc { Type = ProjectileType.Grenade, TrailEffect = "TR_GRENADE", Trail = GrenadeSmoke,
            Body = BodyFamily.GrenadeMesh, ModelPath = "models/grenademodel.md3", GlowColor = GrenadeGreen });
        Add(new Desc { Type = ProjectileType.GrenadeBouncing, TrailEffect = "TR_GRENADE", Trail = GrenadeSmoke,
            Body = BodyFamily.GrenadeMesh, ModelPath = "models/grenademodel.md3", SpinDegPerSec = new Vector3(0, -1000f, 0), GlowColor = GrenadeGreen });
        Add(new Desc { Type = ProjectileType.Mine, TrailEffect = "TR_GRENADE", Trail = GrenadeSmoke,
            Body = BodyFamily.GrenadeMesh, GlowColor = GrenadeGreen });
        // EFFECT_Null — no trail (projectile.qc:354). MDL_PROJECTILE_BLASTER = models/laser.mdl (all.inc:63 —
        // MD3 content despite the .mdl extension), scale 1, no fly loop and no dlight (PROJECTILE_BLASTER is
        // absent from projectile.qc's second per-type switch). Falls back to the additive glow sprite when the
        // laser model isn't mounted (headless / missing content / factory miss).
        Add(new Desc { Type = ProjectileType.Blaster, TrailEffect = "", Trail = null,
            Body = BodyFamily.GlowSprite, ModelPath = "models/laser.mdl", GlowColor = BlasterYellow });
        // EFFECT_Null — no trail. MDL_PROJECTILE_HLAC = models/hlac_bullet.md3 (all.inc:65). Falls back to the
        // additive glow sprite when the bolt model isn't mounted (headless / missing content / factory miss),
        // same convention as Blaster's models/laser.mdl.
        Add(new Desc { Type = ProjectileType.Hlac, TrailEffect = "", Trail = null,
            Body = BodyFamily.GlowSprite, ModelPath = "models/hlac_bullet.md3", GlowColor = new Color(0.9f, 0.7f, 0.3f) });
        // EFFECT_TR_WIZSPIKE (projectile.qc:355,357-358)
        Add(new Desc { Type = ProjectileType.ArcBolt, TrailEffect = "TR_WIZSPIKE", Trail = WizSpike,
            Body = BodyFamily.GlowSprite, GlowColor = new Color(0.5f, 0.9f, 1.0f), HasLight = true });
        Add(new Desc { Type = ProjectileType.PortoRed, TrailEffect = "TR_WIZSPIKE", Trail = WizSpike,
            Body = BodyFamily.GlowSprite, ModelScale = 4f, GlowColor = new Color(1.0f, 0.4f, 0.4f) });
        Add(new Desc { Type = ProjectileType.PortoBlue, TrailEffect = "TR_WIZSPIKE", Trail = WizSpike,
            Body = BodyFamily.GlowSprite, ModelScale = 4f, GlowColor = new Color(0.4f, 0.4f, 1.0f) });
        // EFFECT_TR_KNIGHTSPIKE (projectile.qc:359, forward tumble :135-136)
        Add(new Desc { Type = ProjectileType.Hookbomb, TrailEffect = "TR_KNIGHTSPIKE", Trail = KnightSpike,
            Body = BodyFamily.GrenadeMesh, SpinDegPerSec = new Vector3(1000f, 0, 0), GlowColor = new Color(1.0f, 0.5f, 0.3f) });
        // EFFECT_HAGAR_ROCKET, scale 0.75 (projectile.qc:360-361)
        Add(new Desc { Type = ProjectileType.Hagar, TrailEffect = "HAGAR_ROCKET", Trail = SmallSmoke,
            Body = BodyFamily.RocketMesh, ModelScale = 0.75f, GlowColor = RocketOrange });
        Add(new Desc { Type = ProjectileType.HagarBouncing, TrailEffect = "HAGAR_ROCKET", Trail = SmallSmoke,
            Body = BodyFamily.RocketMesh, ModelScale = 0.75f, GlowColor = RocketOrange });
        // EFFECT_FIREBALL / FIREMINE — particle-only, modelindex 0 (projectile.qc:362-363,463,468)
        Add(new Desc { Type = ProjectileType.Fireball, TrailEffect = "FIREBALL", Trail = FireBig,
            Body = BodyFamily.FireSprite, GlowColor = FireOrange, HasLight = true, LoopSound = "weapons/fireball_fly2" });
        Add(new Desc { Type = ProjectileType.Firemine, TrailEffect = "FIREMINE", Trail = FireSmall,
            Body = BodyFamily.FireSprite, GlowColor = FireOrange, HasLight = true, LoopSound = "weapons/fireball_fly" });
        // EFFECT_TR_ROCKET (tag) (projectile.qc:364)
        Add(new Desc { Type = ProjectileType.Tag, TrailEffect = "TR_ROCKET", Trail = SmallSmoke,
            Body = BodyFamily.GrenadeMesh, GlowColor = new Color(0.8f, 0.8f, 0.4f) });
        // EFFECT_FLAC_TRAIL, scale 0.4 (projectile.qc:365)
        Add(new Desc { Type = ProjectileType.Flac, TrailEffect = "FLAC_TRAIL", Trail = SmallSmoke,
            Body = BodyFamily.GrenadeMesh, ModelScale = 0.4f, GlowColor = new Color(0.8f, 0.8f, 0.5f) });
        // EFFECT_SEEKER_TRAIL, scale 2, seeker_fly loop (projectile.qc:366,483; seeker.qc W_Seeker_Fire_Missile:
        // "missile.scale = 2" — the missile body is drawn at 2× the default size, matching rocket/devastator).
        Add(new Desc { Type = ProjectileType.Seeker, TrailEffect = "SEEKER_TRAIL", Trail = SmallSmoke,
            Body = BodyFamily.RocketMesh, ModelScale = 2f, GlowColor = RocketOrange, LoopSound = "weapons/seeker_fly" });
        // EFFECT_TR_VORESPIKE (projectile.qc:368)
        Add(new Desc { Type = ProjectileType.MageSpike, TrailEffect = "TR_VORESPIKE", Trail = VoreSpike,
            Body = BodyFamily.GlowSprite, GlowColor = new Color(0.7f, 0.4f, 1.0f), HasLight = true });
        // PROJECTILE_GOLEM_LIGHTNING = models/ebomb.mdl (the electro-orb mesh), scale 2.5, EFFECT_TR_NEXUIZPLASMA,
        // random tumble (projectile.qc:369,431-435; golem.qc:145 gren.scale = 2.5). Carries the real ebomb model
        // path (like Electro/RocketMinstaLaser) so the chunk renders the actual mesh, with graceful GlowSprite
        // fallback when the model isn't mounted.
        Add(new Desc { Type = ProjectileType.GolemLightning, TrailEffect = "TR_NEXUIZPLASMA", Trail = BluePlasma,
            Body = BodyFamily.GlowSprite, ModelPath = "models/ebomb.mdl", ModelScale = 2.5f,
            SpinDegPerSec = new Vector3(360f, 480f, 600f),
            GlowColor = new Color(0.5f, 0.7f, 1.0f), HasLight = true });
        // Vehicle projectiles (projectile.qc:373-380)
        Add(new Desc { Type = ProjectileType.SpiderRocket, TrailEffect = "SPIDERBOT_ROCKET_TRAIL", Trail = SmallSmoke,
            Body = BodyFamily.RocketMesh, GlowColor = RocketOrange, LoopSound = "weapons/tag_rocket_fly" });
        Add(new Desc { Type = ProjectileType.WakiRocket, TrailEffect = "RACER_ROCKET_TRAIL", Trail = SmallSmoke,
            Body = BodyFamily.RocketMesh, GlowColor = RocketOrange, LoopSound = "weapons/tag_rocket_fly" });
        Add(new Desc { Type = ProjectileType.WakiCannon, TrailEffect = "", Trail = null,
            Body = BodyFamily.GlowSprite, GlowColor = BlasterYellow });
        // PROJECTILE_RAPTORCANNON (projectiles.inc) — the raptor's energy bolt registers the small blue energy
        // trail (EFFECT_TR_NEXUIZPLASMA), already registered here as BluePlasma. Unlike the WakiCannon (which is
        // genuinely trailless in QC), the raptor cannon carries an in-flight plasma trail + dlight.
        Add(new Desc { Type = ProjectileType.RaptorCannon, TrailEffect = "TR_NEXUIZPLASMA", Trail = SmallSmoke,
            Body = BodyFamily.GlowSprite, GlowColor = BlasterYellow, HasLight = true });
        Add(new Desc { Type = ProjectileType.BumbleGun, TrailEffect = "TR_NEXUIZPLASMA", Trail = BluePlasma,
            Body = BodyFamily.GlowSprite, GlowColor = ElectroBlue, HasLight = true });
        Add(new Desc { Type = ProjectileType.BumbleBeam, TrailEffect = "TR_NEXUIZPLASMA", Trail = BluePlasma,
            Body = BodyFamily.GlowSprite, GlowColor = ElectroBlue, HasLight = true });
        // MDL_PROJECTILE_ROCKETMINSTA_LASER = models/elaser.mdl (all.inc:107), EFFECT_ROCKETMINSTA_LASER trail,
        // team colormod applied per-bolt by the renderer (projectile.qc:384,504-506). Falls back to the GlowSprite
        // when the elaser model isn't mounted (headless / missing content).
        Add(new Desc { Type = ProjectileType.RocketMinstaLaser, TrailEffect = "ROCKETMINSTA_LASER", Trail = LaserRed,
            Body = BodyFamily.GlowSprite, ModelPath = "models/elaser.mdl",
            GlowColor = new Color(1.0f, 0.3f, 0.3f), HasLight = true });
        // Generic plasma (Fireball/Vaporizer "plasma_prim") — blue energy bolt with a light.
        Add(new Desc { Type = ProjectileType.Plasma, TrailEffect = "TR_NEXUIZPLASMA", Trail = BluePlasma,
            Body = BodyFamily.GlowSprite, GlowColor = ElectroBlue, HasLight = true });
        // Held-nade projectiles (cl_nades). QC draws the thrown nade with MDL_PROJECTILE_NADE = grenademodel.md3
        // at scale 1.5 (cl_nades.qc Nade draw) with a random tumble avelocity, tinted by the per-type
        // NadeRegistry m_color (nades.qh REGISTER_NADE m_color). The spin Vector3(0,-600,0) approximates QC's
        // random avelocity tumble. Per-type GlowColor mirrors NadeRegistry.NadeDef.Color exactly. NadeNormal is
        // grey/white with no trail; the elemental nades carry a small matching trail.
        Add(new Desc { Type = ProjectileType.NadeNormal, TrailEffect = "", Trail = null,
            Body = BodyFamily.GrenadeMesh, ModelPath = "models/grenademodel.md3", ModelScale = 1.5f,
            SpinDegPerSec = new Vector3(0, -600f, 0), GlowColor = new Color(1f, 1f, 1f) });
        // Napalm — fire trail (the EF_FLAME / FIREBALL_LASER analog), orange.
        Add(new Desc { Type = ProjectileType.NadeNapalm, TrailEffect = "FIREBALL", Trail = FireSmall,
            Body = BodyFamily.GrenadeMesh, ModelPath = "models/grenademodel.md3", ModelScale = 1.5f,
            SpinDegPerSec = new Vector3(0, -600f, 0), GlowColor = new Color(1f, 0.5f, 0f) });
        Add(new Desc { Type = ProjectileType.NadeIce, TrailEffect = "TR_NEXUIZPLASMA", Trail = BluePlasma,
            Body = BodyFamily.GrenadeMesh, ModelPath = "models/grenademodel.md3", ModelScale = 1.5f,
            SpinDegPerSec = new Vector3(0, -600f, 0), GlowColor = new Color(0f, 0.66f, 1f) });
        Add(new Desc { Type = ProjectileType.NadeTranslocate, TrailEffect = "", Trail = null,
            Body = BodyFamily.GrenadeMesh, ModelPath = "models/grenademodel.md3", ModelScale = 1.5f,
            SpinDegPerSec = new Vector3(0, -600f, 0), GlowColor = new Color(1f, 0f, 1f) });
        Add(new Desc { Type = ProjectileType.NadeSpawn, TrailEffect = "", Trail = null,
            Body = BodyFamily.GrenadeMesh, ModelPath = "models/grenademodel.md3", ModelScale = 1.5f,
            SpinDegPerSec = new Vector3(0, -600f, 0), GlowColor = new Color(1f, 0.9f, 0f) });
        Add(new Desc { Type = ProjectileType.NadeHeal, TrailEffect = "", Trail = null,
            Body = BodyFamily.GrenadeMesh, ModelPath = "models/grenademodel.md3", ModelScale = 1.5f,
            SpinDegPerSec = new Vector3(0, -600f, 0), GlowColor = new Color(1f, 0f, 0f) });
        Add(new Desc { Type = ProjectileType.NadeMonster, TrailEffect = "", Trail = null,
            Body = BodyFamily.GrenadeMesh, ModelPath = "models/grenademodel.md3", ModelScale = 1.5f,
            SpinDegPerSec = new Vector3(0, -600f, 0), GlowColor = new Color(0.1f, 0.65f, 0f) });
        Add(new Desc { Type = ProjectileType.NadeEntrap, TrailEffect = "", Trail = null,
            Body = BodyFamily.GrenadeMesh, ModelPath = "models/grenademodel.md3", ModelScale = 1.5f,
            SpinDegPerSec = new Vector3(0, -600f, 0), GlowColor = new Color(0.4f, 0.85f, 0.15f) });
        Add(new Desc { Type = ProjectileType.NadeVeil, TrailEffect = "", Trail = null,
            Body = BodyFamily.GrenadeMesh, ModelPath = "models/grenademodel.md3", ModelScale = 1.5f,
            SpinDegPerSec = new Vector3(0, -600f, 0), GlowColor = new Color(0.65f, 0.85f, 0.65f) });
        Add(new Desc { Type = ProjectileType.NadeAmmo, TrailEffect = "", Trail = null,
            Body = BodyFamily.GrenadeMesh, ModelPath = "models/grenademodel.md3", ModelScale = 1.5f,
            SpinDegPerSec = new Vector3(0, -600f, 0), GlowColor = new Color(0.33f, 0.33f, 1f) });
        Add(new Desc { Type = ProjectileType.NadeDarkness, TrailEffect = "", Trail = null,
            Body = BodyFamily.GrenadeMesh, ModelPath = "models/grenademodel.md3", ModelScale = 1.5f,
            SpinDegPerSec = new Vector3(0, -600f, 0), GlowColor = new Color(0.23f, 0f, 0.23f) });
        // Generic fallback — neutral glow, light smoke.
        Add(new Desc { Type = ProjectileType.Generic, TrailEffect = "", Trail = SmallSmoke,
            Body = BodyFamily.GlowSprite, GlowColor = new Color(0.85f, 0.85f, 0.9f) });
        return t;
    }

    /// <summary>The descriptor for a projectile type (never null; <see cref="ProjectileType.Generic"/> fallback).</summary>
    public static Desc DescOf(ProjectileType type)
        => Table.TryGetValue(type, out Desc? d) ? d : Table[ProjectileType.Generic];

    // ============================================================================================
    //  Classify an entity → ProjectileType (the server-spawned classname/model is the QC key)
    // ============================================================================================

    /// <summary>DP EF_BLUE (dpextensions.qc:101) — set on a porto projectile rendering as the out-portal (blue)
    /// variant; cleared (EF_RED) for the in-portal (red) variant. The combined shot flips it mid-flight.</summary>
    private const int EfBlue = 64;

    /// <summary>
    /// Resolve a projectile entity to its <see cref="ProjectileType"/> from the server-assigned classname /
    /// model / netname (the same strings the QC keys on). The classnames are the ones the ported weapons set
    /// (rocket, grenade, spike, electro_bolt, mine, seeker_missile, …); falls back to <see cref="ProjectileType.Generic"/>.
    /// </summary>
    public static ProjectileType Classify(Entity e)
    {
        if (e is null) return ProjectileType.Generic;
        string s = (e.ClassName + " " + e.Model + " " + e.NetName).ToLowerInvariant();

        // Vehicle rockets first (more specific than the generic "rocket"/"missile").
        if (Has(s, "spiderbot_rocket", "spiderrocket")) return ProjectileType.SpiderRocket;
        if (Has(s, "wakizashi_rocket", "wakirocket", "racer_rocket")) return ProjectileType.WakiRocket;
        if (Has(s, "raptorcannon")) return ProjectileType.RaptorCannon;

        // electro_bolt is the PRIMARY (PROJECTILE_ELECTRO_BEAM: elaser model, no fly loop, electro_impact);
        // electro_orb the SECONDARY ball (PROJECTILE_ELECTRO: ebomb model, electro_fly loop, ballexplode).
        if (Has(s, "electro_bolt", "elaser")) return ProjectileType.ElectroBeam;
        if (Has(s, "electro_orb", "electro")) return ProjectileType.Electro;
        // The RocketMinsta laser bolt networks netname "rocketminsta" (Vaporizer.RocketMinstaLaserBarrage); its
        // distinct PROJECTILE_ROCKETMINSTA_LASER visual (elaser model + red ROCKETMINSTA_LASER trail + team
        // colormod) MUST be matched BEFORE the generic "rocket" check below — otherwise the "rocket" substring
        // inside "rocketminsta" misclassifies the bolt as a Devastator rocket (smoke trail + rocket-fly loop).
        if (Has(s, "rocketminsta")) return ProjectileType.RocketMinstaLaser;
        if (Has(s, "devastator", "rocket")) return ProjectileType.Rocket;
        if (Has(s, "rpc")) return ProjectileType.Rpc;
        // The mage spike sets NetName "mage_spike" (Mage.cs); the key string contains the substring "spike",
        // so check "mage" BEFORE the generic crylink "spike" branch below — otherwise the in-flight spike is
        // misclassified as a Crylink bolt (purple shards) instead of PROJECTILE_MAGE_SPIKE (TR_VORESPIKE).
        if (Has(s, "mage")) return ProjectileType.MageSpike;
        if (Has(s, "spike", "crylink")) return ProjectileType.Crylink;
        if (Has(s, "hookbomb")) return ProjectileType.Hookbomb;
        if (Has(s, "grapplinghook", "hook")) return ProjectileType.Hookbomb;
        if (Has(s, "mine")) return ProjectileType.Mine;
        // Held-nade projectiles (cl_nades). The server stamps the nade entity's classname "nade" and appends the
        // per-type netname (ServerNet → key "nade <netname>"), so the key carries the Nades-registry token. This
        // MUST be matched BEFORE the mortar "grenade" branch below: the mortar's classname is "grenade" (which
        // contains the substring "nade"), so the held nade is told apart by its standalone "nade" classname (the
        // mortar's "grenade" never yields a bare "nade" token) PLUS the per-type netname. A bare "nade" with no
        // type token is the default NadeNormal.
        if (HasNadeClass(s))
        {
            if (Has(s, "napalm")) return ProjectileType.NadeNapalm;
            if (Has(s, "ice")) return ProjectileType.NadeIce;
            if (Has(s, "translocate")) return ProjectileType.NadeTranslocate;
            if (Has(s, "spawn")) return ProjectileType.NadeSpawn;
            if (Has(s, "heal")) return ProjectileType.NadeHeal;
            if (Has(s, "pokenade", "monster")) return ProjectileType.NadeMonster;
            if (Has(s, "entrap")) return ProjectileType.NadeEntrap;
            if (Has(s, "veil")) return ProjectileType.NadeVeil;
            if (Has(s, "ammo")) return ProjectileType.NadeAmmo;
            if (Has(s, "darkness")) return ProjectileType.NadeDarkness;
            return ProjectileType.NadeNormal; // bare "nade"/"normal" — the default held nade
        }
        // The bouncing mortar grenade (type 1) networks a "bouncing" token (ServerNet) so it draws the
        // sideways-tumbling PROJECTILE_GRENADE_BOUNCING model rather than the plain PROJECTILE_GRENADE.
        if (Has(s, "grenade", "mortar"))
            return Has(s, "bouncing") ? ProjectileType.GrenadeBouncing : ProjectileType.Grenade;
        if (Has(s, "hagar")) return ProjectileType.Hagar;
        if (Has(s, "seeker_tag", "tag_tracker", "tag")) return ProjectileType.Tag;
        if (Has(s, "seeker_missile", "seeker")) return ProjectileType.Seeker;
        if (Has(s, "flac")) return ProjectileType.Flac;
        if (Has(s, "firemine", "firemine")) return ProjectileType.Firemine;
        // The wyvern's monster_projectile networks netname "wyvern"; QC wr_think draws it as
        // CSQCProjectile(..., PROJECTILE_FIREMINE, ...) — the spinning fire-mine visual (FireSprite body,
        // FireOrange glow + light, FIREMINE trail). Keyed before "fireball" so the netname resolves here.
        if (Has(s, "wyvern")) return ProjectileType.Firemine;
        if (Has(s, "fireball")) return ProjectileType.Fireball;
        // The porto projectile networks its red (in-portal) / blue (out-portal) state in Entity.Effects via
        // the DP EF_RED (128) / EF_BLUE (64) bits, flipped mid-flight when a combined shot lays its in-portal
        // and continues as the out-portal (porto.qc:242-243, CSQCProjectile(...PROJECTILE_PORTO_BLUE)). The
        // server seeds type<=0 as RED and type 1 as BLUE; we key on the effect bit so the blue variant renders.
        if (Has(s, "porto"))
            return (e.Effects & EfBlue) != 0 ? ProjectileType.PortoBlue : ProjectileType.PortoRed;
        if (Has(s, "arc")) return ProjectileType.ArcBolt;
        if (Has(s, "hlacbolt", "hlac")) return ProjectileType.Hlac;
        // (rocketminsta handled above, before the generic "rocket" check; mage handled above, before "spike")
        if (Has(s, "golem")) return ProjectileType.GolemLightning;
        if (Has(s, "plasma", "vaporizer", "minsta")) return ProjectileType.Plasma;
        if (Has(s, "blaster", "laser")) return ProjectileType.Blaster;
        return ProjectileType.Generic;
    }

    private static bool Has(string hay, params string[] needles)
    {
        foreach (string n in needles)
            if (hay.Contains(n, StringComparison.Ordinal)) return true;
        return false;
    }

    /// <summary>
    /// True when the key carries a held-nade classname ("nade"/"fake_nade") rather than a mortar "grenade".
    /// The mortar grenade's classname "grenade" contains the substring "nade", so a naive Contains("nade")
    /// would misfire — we require a standalone "nade" token (start-of-key or space-prefixed) so only the held
    /// nade's classname matches. (The key is "classname model netname", lowercased, space-separated.)
    /// </summary>
    private static bool HasNadeClass(string s)
        => s.StartsWith("nade", StringComparison.Ordinal) || s.Contains(" nade", StringComparison.Ordinal);

    // ============================================================================================
    //  Client-side collision behaviour (ProjectilePredictor world trace)
    // ============================================================================================

    /// <summary>How a predicted projectile reacts to world geometry while extrapolating between snapshots.</summary>
    public enum CollisionMode
    {
        /// <summary>Fly straight; let the authoritative snapshot/removal handle impact (no client trace).</summary>
        None,
        /// <summary>Stop at the surface (a detonate-on-impact FLY flier — the server's removal lands a moment later).</summary>
        Stop,
        /// <summary>Reflect off the surface and keep flying (an elastic, gravity-free <c>BOUNCEMISSILE</c>).</summary>
        Bounce,
    }

    /// <summary>
    /// The client-side collision behaviour for a projectile type. Only the cases where straight-line
    /// extrapolation is geometrically exact opt in: <b>Stop</b> for detonate-on-impact <c>MOVETYPE_FLY</c>
    /// fliers (rocket/blaster/crylink/hagar/seeker/…) so a fast bolt doesn't overrun a wall before the
    /// server's removal arrives, and <b>Bounce</b> for gravity-free <c>MOVETYPE_BOUNCEMISSILE</c> projectiles
    /// (bouncing crylink/arc/hagar, porto) whose wall reflections are planar. The gravity-affected bouncers
    /// (grenade/mine/electro/hookbomb — <c>MOVETYPE_TOSS/BOUNCE</c>) stay <b>None</b> (straight + snapshot
    /// correction, no regression) until client-side gravity integration lands — see the predictor remarks.
    /// </summary>
    public static CollisionMode CollisionFor(ProjectileType t) => t switch
    {
        ProjectileType.Rocket or ProjectileType.Rpc or ProjectileType.Blaster or ProjectileType.Crylink
            or ProjectileType.Hagar or ProjectileType.Seeker or ProjectileType.Plasma or ProjectileType.Hlac
            or ProjectileType.RocketMinstaLaser or ProjectileType.Fireball or ProjectileType.Flac
            or ProjectileType.Tag or ProjectileType.SpiderRocket or ProjectileType.WakiRocket
            or ProjectileType.WakiCannon or ProjectileType.BumbleGun or ProjectileType.MageSpike
            or ProjectileType.GolemLightning or ProjectileType.ElectroBeam => CollisionMode.Stop,

        ProjectileType.CrylinkBouncing or ProjectileType.ArcBolt or ProjectileType.HagarBouncing
            or ProjectileType.PortoRed or ProjectileType.PortoBlue => CollisionMode.Bounce,

        // The held nades are MOVETYPE_BOUNCE gravity bouncers (like the grenade), so they stay None (straight
        // + snapshot correction) until client-side gravity integration lands — same as grenade/mine.
        ProjectileType.NadeNormal or ProjectileType.NadeNapalm or ProjectileType.NadeIce
            or ProjectileType.NadeTranslocate or ProjectileType.NadeSpawn or ProjectileType.NadeHeal
            or ProjectileType.NadeMonster or ProjectileType.NadeEntrap or ProjectileType.NadeVeil
            or ProjectileType.NadeAmmo or ProjectileType.NadeDarkness => CollisionMode.None,

        _ => CollisionMode.None, // gravity TOSS/BOUNCE (grenade/mine/electro/hookbomb/firemine) + unknowns
    };
}
