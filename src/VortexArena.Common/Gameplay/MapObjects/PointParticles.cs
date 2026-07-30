// Port of qcsrc/common/mapobjects/func/pointparticles.qc — SVQC half (func_pointparticles + func_sparks).
//
// func_pointparticles continuously emits a named effectinfo effect (.mdl) from a point or throughout a
// brush volume at .impulse emissions per second (negative = RELATIVE density, particles per 64³ cube),
// each emission spawning .count's worth of the effect; .velocity/.movedir/.waterlevel shape the spray.
// func_sparks is the convenience wrapper with spark defaults. The EMISSION itself is client-side
// (QC Draw_PointParticles — see game/client/MapParticleEmitters.cs); the server holds state + toggling.
//
// Port notes:
//  * QC resolves .mdl to an effect number lazily client-side (cnt=0 "use a good handler"); the port keeps
//    the NAME on Entity.Mdl and the client resolves it from effectinfo.
//  * pointparticles_think only nets origin moves (SendFlags) — unnecessary on the shared-entity seam, so
//    no think is installed.
//  * .noise (a per-emission positional sound) and bgmscript ADSR are out of scope (documented residual).

using System.Numerics;
using XonoticGodot.Common.Framework;
using XonoticGodot.Common.Services;

namespace XonoticGodot.Common.Gameplay;

/// <summary><c>func_pointparticles</c> / <c>func_sparks</c>. Registered by <see cref="MapObjectsRegistry"/>.</summary>
public static class PointParticles
{
    // ---- spawnflags (pointparticles.qh:4-5 + defs.qh START_ENABLED) ----
    public const int StartOn = 1 << 0;       // START_ON (via generic_netlinked_reset)
    public const int ParticlesImpulse = 1 << 1;     // PARTICLES_IMPULSE — absolute burst of .impulse on toggle-on only
    public const int ParticlesVisCulling = 1 << 2;  // PARTICLES_VISCULLING — network culling hint (no-op here)

    /// <summary><c>spawnfunc(func_pointparticles)</c> (pointparticles.qc:93-127).</summary>
    public static void PointParticlesSetup(Entity this_)
    {
        // func_sparks chains through here and must KEEP its classname (QC spawnfunc chaining never retags).
        if (this_.ClassName != "func_sparks")
            this_.ClassName = "func_pointparticles";

        // model set => brush volume (modelindex + mins/maxs from the inline brush).
        if (Api.Services is not null && !string.IsNullOrEmpty(this_.Model))
            Api.Entities.SetModel(this_, this_.Model);

        // (mdl set => cnt = 0, "use a good handler" — the name on Entity.Mdl IS the handler port-side.)

        // --- defaults (pointparticles.qc:102-106) ---
        if (this_.Atten == 0f)
            this_.Atten = 0.5f;   // ATTEN_NORM
        else if (this_.Atten < 0f)
            this_.Atten = 0f;     // global
        if (this_.Volume == 0f)
            this_.Volume = 1f;
        if (this_.ParticleCount == 0f)
            this_.ParticleCount = 1f;
        if (this_.Impulse == 0f)
            this_.Impulse = 1f;

        // no brush model => an explicit mins/maxs box: re-anchor the origin at the box min (108-112).
        if (this_.ModelIndex == 0)
        {
            MapMover.SetOrigin(this_, this_.Origin + this_.Mins);
            MapMover.SetSize(this_, Vector3.Zero, this_.Maxs - this_.Mins);
        }

        this_.MoveType = MoveType.None;
        this_.SetActive = LogicGates.GenericSetActive; // generic_netlinked_setactive (SendFlags-only diff)

        if (!string.IsNullOrEmpty(this_.TargetName))
            this_.Use = LegacyUse; // backwards compatibility (generic_netlinked_legacy_use)

        // generic_netlinked_reset: targetname set => active iff START_ON; else always on.
        if (!string.IsNullOrEmpty(this_.TargetName))
            this_.Active = (this_.SpawnFlags & StartOn) != 0 ? MapMover.ActiveActive : MapMover.ActiveNot;
        else
            this_.Active = MapMover.ActiveActive;

        MapMover.IndexRegister(this_);
    }

    /// <summary><c>spawnfunc(func_sparks)</c> (pointparticles.qc:129-146) — spark-fountain defaults, then chains.</summary>
    public static void SparksSetup(Entity this_)
    {
        this_.ClassName = "func_sparks";

        if (this_.ParticleCount < 1f)
            this_.ParticleCount = 25f;  // nice default value (QC)
        if (this_.Impulse < 0.5f)
            this_.Impulse = 2.5f;       // nice default value (QC)

        this_.Mins = Vector3.Zero;
        this_.Maxs = Vector3.Zero;
        this_.Size = Vector3.Zero;
        this_.Velocity = new Vector3(0f, 0f, -1f);
        this_.Mdl = "TE_SPARK";
        // (QC cnt = 0 — use mdl.)

        PointParticlesSetup(this_);
    }

    /// <summary><c>generic_netlinked_legacy_use</c> (triggers.qc): old maps toggle via a direct trigger.</summary>
    private static void LegacyUse(Entity self, Entity actor)
    {
        if (self.SetActive is { } sa)
            sa(self, MapMover.ActiveToggle);
        else
            LogicGates.GenericSetActive(self, MapMover.ActiveToggle);
    }
}
