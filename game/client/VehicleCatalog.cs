using Godot;

namespace VortexArena.Game.Client;

/// <summary>
/// Per-vehicle visual specs — the data the <see cref="VehicleVisuals"/> driver needs to reproduce the
/// vehicle presentation the libs flag with the bulk of the 27 <c>TODO(port,client)</c> markers
/// (Base/.../qcsrc/common/vehicles/vehicle/*.qc <c>vr_spawn</c>/<c>*_frame</c>/<c>vr_death</c>): the Raptor's
/// counter-rotating rotor spinners + engine speed sound, the Spiderbot's spinning minigun barrels + frame-
/// driven legs + head/gun death gibs, the Racer's scale-0.5 model + idle/move/boost engine crossfade, and the
/// Bumblebee's networked heal-beam + gibs.
///
/// Every field cites its QC origin. Tag names are the model attachment tags the QC uses
/// (<c>setattachment</c>/<c>gettaginfo</c>); when the resolved model doesn't expose a tag the driver falls
/// back to the configured offset, so the mechanism is visible even without the exact <c>.dpm</c> art.
/// </summary>
public static class VehicleCatalog
{
    public enum VehicleKind { None, Racer, Raptor, Spiderbot, Bumblebee }

    /// <summary>A spinning sub-model (the Raptor's two props). QC <c>raptor_spinner</c> avelocity '0 ±90 0'.</summary>
    public readonly record struct RotorSpec(string Tag, Vector3 FallbackOffset, Vector3 SpinDegPerSec);

    /// <summary>A gun hardpoint (mount tag + optional spinning barrel tag, e.g. the Spiderbot minigun barrels).</summary>
    public readonly record struct GunSpec(string Tag, Vector3 FallbackOffset, string? BarrelTag);

    /// <summary>The engine loop sound set (QC SND_VEH_* engine sounds), crossfaded by speed.</summary>
    public readonly record struct EngineSounds(string Idle, string Move, string? Boost);

    /// <summary>One vehicle type's full visual descriptor.</summary>
    public sealed class Desc
    {
        public required VehicleKind Kind;
        public string Model = "";
        /// <summary>QC model <c>scale</c> (Racer's "scale-0.5 model hack"; 1 for the rest).</summary>
        public float Scale = 1f;
        public RotorSpec[] Rotors = System.Array.Empty<RotorSpec>();
        public GunSpec[] Guns = System.Array.Empty<GunSpec>();
        public EngineSounds Engine;
        /// <summary>Legs/body are frame-driven (the Spiderbot's walk/strafe/idle/jump frames), not static.</summary>
        public bool FrameDrivenBody;
        /// <summary>This vehicle fires a networked heal-beam from <see cref="HealGunTag"/> (the Bumblebee BRG_*).</summary>
        public bool HealBeam;
        public string HealGunTag = "fire";
        /// <summary>Muzzle effect for the primary gun (QC EFFECT_*_MUZZLEFLASH).</summary>
        public string MuzzleEffect = "";
        /// <summary>Number of debris chunks spawned on death (QC vr_death gib entities), and their tint.</summary>
        public int GibCount = 6;
        public Color GibTint = new(0.25f, 0.25f, 0.28f);
        /// <summary>Death sound (QC SND_VEH_*_DIE / the rocket-impact tumble loop).</summary>
        public string DeathSound = "";
    }

    // ============================================================================================
    //  The table
    // ============================================================================================

    private static readonly System.Collections.Generic.Dictionary<VehicleKind, Desc> Table = Build();

    private static System.Collections.Generic.Dictionary<VehicleKind, Desc> Build()
    {
        var t = new System.Collections.Generic.Dictionary<VehicleKind, Desc>();

        // Raptor (raptor.qc vr_spawn): two counter-rotating prop spinners at engine_left/right (±90°/s about Y),
        // two guns at gunmount_left/right, the raptor_speed engine sound. (raptor.qc:688-701, 447)
        t[VehicleKind.Raptor] = new Desc
        {
            Kind = VehicleKind.Raptor,
            Model = "models/vehicles/raptor.dpm",
            Rotors = new[]
            {
                new RotorSpec("engine_left",  new Vector3(-42f, 14f, -6f), new Vector3(0f, 900f, 0f)),
                new RotorSpec("engine_right", new Vector3( 42f, 14f, -6f), new Vector3(0f, -900f, 0f)),
            },
            Guns = new[]
            {
                new GunSpec("gunmount_left",  new Vector3(-18f, -6f, -22f), null),
                new GunSpec("gunmount_right", new Vector3( 18f, -6f, -22f), null),
            },
            Engine = new EngineSounds("vehicles/raptor_fly", "vehicles/raptor_speed", null),
            MuzzleEffect = "RAPTOR_MUZZLEFLASH",
            GibCount = 7,
            DeathSound = "weapons/rocket_impact",
        };

        // Spiderbot (spiderbot.qc): frame-driven legs (idle/walk/strafe/jump), two minigun guns whose "barrels"
        // tag spins when firing, head hardpoints, the locomotion sound set. (spiderbot.qc:85-89,122-274)
        t[VehicleKind.Spiderbot] = new Desc
        {
            Kind = VehicleKind.Spiderbot,
            Model = "models/vehicles/spiderbot.dpm",
            FrameDrivenBody = true,
            Guns = new[]
            {
                new GunSpec("tag_hardpoint01", new Vector3(-20f, 24f, 18f), "barrels"),
                new GunSpec("tag_hardpoint02", new Vector3( 20f, 24f, 18f), "barrels"),
            },
            Engine = new EngineSounds("vehicles/spiderbot_idle", "vehicles/spiderbot_walk", "vehicles/spiderbot_strafe"),
            MuzzleEffect = "SPIDERBOT_MINIGUN_MUZZLEFLASH",
            GibCount = 6,
            DeathSound = "vehicles/spiderbot_die",
        };

        // Racer/Wakizashi (racer.qc): the scale-0.5 model hack, four hover engines (tag_engine_*), the
        // idle/move/boost engine sounds, rocket lock aux-crosshair coloring. (racer.qc:93-103, vr_spawn)
        t[VehicleKind.Racer] = new Desc
        {
            Kind = VehicleKind.Racer,
            Model = "models/vehicles/wakizashi.dpm",
            Scale = 0.5f,
            Engine = new EngineSounds("vehicles/racer_idle", "vehicles/racer_move", "vehicles/racer_boost"),
            MuzzleEffect = "RACER_MUZZLEFLASH",
            GibCount = 5,
            DeathSound = "weapons/rocket_impact",
        };

        // Bumblebee (bumblebee.qc): the networked BRG_* heal-beam from gun3 "fire" tag to the heal target,
        // engine sound, the gun1/gun2/gun3 + body gibs on death. (bumblebee.qc:9-11,180,550-558)
        t[VehicleKind.Bumblebee] = new Desc
        {
            Kind = VehicleKind.Bumblebee,
            Model = "models/vehicles/bumblebee_body.dpm",
            Guns = new[]
            {
                new GunSpec("tag_fire1", new Vector3(-34f, -10f, 4f), null),
                new GunSpec("tag_fire2", new Vector3( 34f, -10f, 4f), null),
            },
            Engine = new EngineSounds("vehicles/raptor_fly", "vehicles/raptor_speed", null),
            HealBeam = true,
            HealGunTag = "fire",
            MuzzleEffect = "BIGPLASMA_MUZZLEFLASH",
            GibCount = 8,
            DeathSound = "weapons/rocket_impact",
        };

        return t;
    }

    /// <summary>The descriptor for a vehicle kind, or null for <see cref="VehicleKind.None"/>.</summary>
    public static Desc? DescOf(VehicleKind kind) => Table.TryGetValue(kind, out Desc? d) ? d : null;

    /// <summary>Classify a vehicle by its classname / model (the QC vehicle name); None if not a vehicle.</summary>
    public static VehicleKind Classify(string classNameOrModel)
    {
        string s = classNameOrModel.ToLowerInvariant();
        if (s.Contains("raptor")) return VehicleKind.Raptor;
        if (s.Contains("spiderbot") || s.Contains("spider")) return VehicleKind.Spiderbot;
        if (s.Contains("racer") || s.Contains("wakizashi")) return VehicleKind.Racer;
        if (s.Contains("bumble")) return VehicleKind.Bumblebee;
        return VehicleKind.None;
    }
}
