namespace XonoticGodot.Engine.Particles;

// =====================================================================================================
//  Cvar name constants + stock defaults for the dual particle system. Centralized so every module
//  (faithful sim, modern backend, SDF service, router, menu) references the same strings — no typo drift.
//  Defaults are registered into the ICvarService at boot (game/menu/framework/ClientSettings.cs wires
//  Register(name, default, Save) for each entry in Defaults).
// =====================================================================================================

/// <summary>Cvar names + defaults for the dual particle system (planning/particles-dual-system.md §overview).</summary>
public static class ParticleCvars
{
    // --- dual-system control (new) ---
    public const string Modern = "cl_particles_modern";                  // 0=original, 1=mixed, 2=modern
    public const string SdfGenerate = "cl_particles_sdf_generate";       // allow SDF gen at map load
    public const string ModernNoSdf = "cl_particles_modern_nosdf";       // 1=modern collisionless when no SDF, 0=reroute faithful
    public const string SdfChunk = "cl_particles_sdf_chunk";             // chunk edge length (qu)
    public const string SdfVoxel = "cl_particles_sdf_voxel";             // target voxel size (qu)
    public const string SdfDebug = "cl_particles_sdf_debug";             // draw chunk AABBs + gen stats

    // --- DP mirrors (faithful backend, §C.6) ---
    public const string Particles = "cl_particles";
    public const string Quality = "cl_particles_quality";
    public const string Alpha = "cl_particles_alpha";
    public const string Size = "cl_particles_size";
    public const string Collisions = "cl_particles_collisions";
    public const string Blood = "cl_particles_blood";
    public const string BloodAlpha = "cl_particles_blood_alpha";
    public const string Sparks = "cl_particles_sparks";
    public const string Smoke = "cl_particles_smoke";
    public const string SmokeAlpha = "cl_particles_smoke_alpha";
    public const string SmokeAlphaFade = "cl_particles_smoke_alphafade";
    public const string Bubbles = "cl_particles_bubbles";
    public const string Rain = "cl_particles_rain";
    public const string Snow = "cl_particles_snow";
    public const string BulletImpacts = "cl_particles_bulletimpacts";
    public const string Decals = "cl_decals";
    public const string DecalsImmediateBloodStain = "cl_decals_newsystem_immediatebloodstain";
    public const string DecalsBloodSmears = "cl_decals_newsystem_bloodsmears";
    public const string DrawDistance = "r_drawparticles_drawdistance";
    public const string NearClipMin = "r_drawparticles_nearclip_min";
    public const string NearClipMax = "r_drawparticles_nearclip_max";

    /// <summary>(name, default, archived) for every particle cvar — registered at boot.</summary>
    public static readonly (string Name, string Default, bool Archived)[] Defaults =
    {
        // dual-system control
        (Modern, "0", true),
        (SdfGenerate, "1", true),
        (ModernNoSdf, "1", true),
        (SdfChunk, "1024", true),
        (SdfVoxel, "8", true),
        (SdfDebug, "0", false),
        // DP mirrors
        (Particles, "1", true),
        (Quality, "1", true),
        (Alpha, "1", true),
        (Size, "1", true),
        (Collisions, "1", true),
        (Blood, "1", true),
        (BloodAlpha, "1", true),
        (Sparks, "1", true),
        (Smoke, "1", true),
        (SmokeAlpha, "0.5", true),
        (SmokeAlphaFade, "0.55", true),
        (Bubbles, "1", true),
        (Rain, "1", true),
        (Snow, "1", true),
        (BulletImpacts, "1", true),
        (Decals, "1", true),
        (DecalsImmediateBloodStain, "2", true),
        (DecalsBloodSmears, "1", true),
        (DrawDistance, "2000", true),   // DP stock r_drawparticles_drawdistance (gate scales by size², cl_particles.c:3158)
        (NearClipMin, "4", true),
        (NearClipMax, "4", true),       // == min by default → hard clip, no fade band (DP stock)
    };
}
