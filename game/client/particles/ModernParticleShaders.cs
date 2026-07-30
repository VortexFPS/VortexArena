using System;
using System.Collections.Generic;
using System.Text;
using Godot;

namespace VortexArena.Game.Client.Particles;

// =====================================================================================================
//  ModernParticleShaders — the INLINE Godot shaders for the modern GPU backend (planning/
//  particles-dual-system.md §B). Two shaders:
//
//   (a) the PROCESS shader (shader_type particles) — start() implements DP's CL_NewParticle /
//       CL_NewParticlesFromEffectinfo spawn (Base/darkplaces/cl_particles.c:1754-1781 + :668-849);
//       process() implements DP's R_DrawParticles per-frame integration in DP's exact order
//       (cl_particles.c:2958-3105) plus the §B.2 SDF collision response (COLLIDED / COLLISION_NORMAL /
//       COLLISION_DEPTH), reproducing DP's bounce semantics (NOT Godot restitution).
//
//   (b) the DRAW shader (shader_type spatial) — a camera-facing billboard (the same vertex math as the
//       existing growth shader in EffectSystem.cs) with atlas-cell sampling, the three blend modes
//       (alpha/add/sub≈invmod), an optional SOFT-PARTICLES depth-fade branch (DEPTH_TEXTURE), an optional
//       LIT path, a flipbook UV-advance branch, velocity-stretched spark orientation, and an EmissiveBoost
//       branch for the existing HDR/bloom.
//
//  All shader source is built as inline C# strings (`new Shader { Code = @"..." }`) — this repo has NO
//  .gdshader files (see EffectSystem.GrowthShader). Compiled shaders are cached by FEATURE SHAPE (which
//  features are nonzero) so two emitters that need the same branches share one compiled pipeline; the cheap
//  per-emitter ShaderMaterial then just carries the uniform values.
//
//  Coordinate note: the backend feeds all spawn parameters ALREADY in Godot space (Y-up). Gravity is applied
//  to VELOCITY.y (Godot up axis) with DP's 800 qu/s² constant and the block's gravity MULTIPLIER, matching
//  the §B.1 spec (1 qu == 1 Godot unit). The host's Coords bridge (game/Coords.cs) does the axis swap before
//  the uniforms are set, so the shader math is axis-agnostic.
//
//  CUSTOM channel allocation (the only 4 floats shared start()->process()->draw). NOTE: Godot particles
//  shaders have NO `AGE` builtin and `RESTART` is process-only — start() runs once at spawn, so we track age
//  ourselves in CUSTOM.y (incremented by DELTA each process) and derive the lifetime phase in the draw shader:
//    CUSTOM.x = current rotation angle (radians)         — integrated by spin in process(), read by draw vertex
//    CUSTOM.y = age seconds (incremented by DELTA)       — draw derives phase = CUSTOM.y / CUSTOM.w (growth/flipbook)
//    CUSTOM.z = spawn alpha 0..1 (lhrandom alpha / 256)  — the alphafade reference for COLOR.a each frame
//    CUSTOM.w = per-particle life seconds                — DP's per-particle lifetime (NOT the emitter LIFETIME)
// =====================================================================================================

/// <summary>Builds + caches the modern backend's inline process (particles) and draw (spatial) shaders,
/// keyed by feature shape. Public surface: <see cref="ProcessFeatures"/>/<see cref="DrawFeatures"/> describe
/// the enabled branches; <see cref="GetProcessShader"/>/<see cref="GetDrawShader"/> return the cached Shader.</summary>
public static class ModernParticleShaders
{
    // DP world gravity (sv_gravity default, qu/s²). 1 qu == 1 Godot unit, so this is the Y-up acceleration.
    // (cl_particles.c R_DrawParticles: gravity = frametime * cl.movevars_gravity, movevars_gravity default 800.)
    public const float DpGravity = 800f;

    // ------------------------------------------------------------------------------------------------
    //  Feature shapes (the cache keys)
    // ------------------------------------------------------------------------------------------------

    /// <summary>Which optional branches the PROCESS shader needs — derived from the block's nonzero fields so
    /// effects with the same shape share one compiled pipeline (e.g. a block with no friction/bounce/curl gets
    /// the lean integrator). Equality is by value (record struct) → it doubles as the shader-cache key.</summary>
    public readonly record struct ProcessFeatures
    {
        public bool AirFriction { get; init; }   // cl_particles.c:2972-2976 velocity-proportional decay
        public bool Bounce { get; init; }         // §B.2 SDF response (bounce != 0)
        public bool BloodSplat { get; init; }     // bounce < 0 → emit_subparticle splat sub-emitter
        public bool Spark { get; init; }          // pt_spark: live velocity stretch + spark slow-kill
        public bool Snow { get; init; }           // pt_snow flutter (incl. the DP vel.y-from-vel.x bug)
        public bool SizeIncrease { get; init; }   // per-particle scale over AGE (drives CUSTOM.y growth phase)
        public bool Curl { get; init; }           // curl-noise turbulence (modern preset)
        public bool Spin { get; init; }           // angular velocity (rotate spin ranges)

        /// <param name="collisionEnabled">When false, the §B.2 SDF-response branches (bounce / blood-splat) are
        /// NOT compiled in, so the emitter ignores any COLLIDED report even though Godot's colliders may still
        /// flag it — a collisionless modern emitter (cl_particles_modern_nosdf 1 path). Decoupling collision
        /// from the render layer is required because the emitter must stay on a camera-visible layer (see
        /// <see cref="!:ModernParticleBackend.ModernCollisionLayer"/>).</param>
        public static ProcessFeatures From(in ModernEmitterParams p, in ModernPreset preset, bool collisionEnabled) => new()
        {
            AirFriction = MathF.Abs(p.AirFriction) > 1e-6f,
            Bounce = collisionEnabled && MathF.Abs(p.Bounce) > 1e-6f,
            BloodSplat = collisionEnabled && p.Bounce < -1e-6f,
            Spark = p.IsSpark,
            Snow = p.IsSnow,
            // Sparks manage their basis entirely via the live velocity-stretch (column lengths are the streak
            // dims, not a uniform size), so the per-particle sizeincrease scaling is skipped for them — matching
            // the faithful path's ComputeGrowthRatio, which returns null for sparks.
            SizeIncrease = !p.IsSpark && MathF.Abs(p.SizeIncrease) > 1e-6f,
            Curl = preset.CurlNoise > 1e-4f,
            Spin = MathF.Abs(p.SpinMin) > 1e-6f || MathF.Abs(p.SpinMax) > 1e-6f,
        };
    }

    /// <summary>Which optional branches the DRAW (spatial) shader needs — soft particles, lit shading, flipbook
    /// animation, spark orientation, emissive boost, and the blend mode (baked into render_mode). Value key.</summary>
    public readonly record struct DrawFeatures
    {
        public ModernBlend Blend { get; init; }
        public bool Soft { get; init; }
        public bool Lit { get; init; }
        public bool Flipbook { get; init; }
        public bool Spark { get; init; }          // velocity-stretched quad (orientation = spark)
        public bool EmissiveBoost { get; init; }  // EmissiveBoost != 1

        public static DrawFeatures From(in ModernEmitterParams p, in ModernPreset preset) => new()
        {
            Blend = p.Blend,
            Soft = preset.SoftParticles,
            Lit = preset.Lit,
            Flipbook = preset.Flipbook,
            Spark = p.IsSpark,
            EmissiveBoost = MathF.Abs(preset.EmissiveBoost - 1f) > 1e-3f,
        };
    }

    /// <summary>Godot draw blend mode (mirrors the EiBlend → BaseMaterial3D.BlendMode mapping the faithful path
    /// uses: alpha → Mix, add → Add, invmod → Sub).</summary>
    public enum ModernBlend { Alpha, Add, Sub }

    // ------------------------------------------------------------------------------------------------
    //  Caches
    // ------------------------------------------------------------------------------------------------

    private static readonly Dictionary<ProcessFeatures, Shader> _processCache = new();
    private static readonly Dictionary<DrawFeatures, Shader> _drawCache = new();

    /// <summary>The cached process (particles) shader for a feature shape. Built once per distinct shape.</summary>
    public static Shader GetProcessShader(in ProcessFeatures f)
    {
        if (_processCache.TryGetValue(f, out Shader? cached))
            return cached;
        var shader = new Shader { Code = BuildProcessShader(f) };
        _processCache[f] = shader;
        return shader;
    }

    /// <summary>The cached draw (spatial) shader for a feature shape. Built once per distinct shape.</summary>
    public static Shader GetDrawShader(in DrawFeatures f)
    {
        if (_drawCache.TryGetValue(f, out Shader? cached))
            return cached;
        var shader = new Shader { Code = BuildDrawShader(f) };
        _drawCache[f] = shader;
        return shader;
    }

    /// <summary>Forget all compiled shaders (test/teardown hook; the live game keeps them resident).</summary>
    public static void ClearCache()
    {
        _processCache.Clear();
        _drawCache.Clear();
    }

    // =================================================================================================
    //  (a) PROCESS SHADER — shader_type particles
    // =================================================================================================

    private static string BuildProcessShader(in ProcessFeatures f)
    {
        var sb = new StringBuilder(4096);

        sb.Append("shader_type particles;\n");
        // keep_data: do NOT zero TRANSFORM/VELOCITY/CUSTOM between frames — we integrate them ourselves and rely
        // on the persisted state (the integrator is semi-implicit, DP's order; Godot's auto-integration is off
        // because we never call its built-in velocity application — we move TRANSFORM[3] by hand).
        sb.Append("render_mode keep_data, disable_velocity;\n\n");

        // --- uniforms = the EffectInfoEmitter fields (the §B.1 contract). All already in Godot space. ---------
        sb.Append("// Spawn ranges (lhrandom min..max) — DP cl_particles.c:668-849.\n");
        sb.Append("uniform vec3 origin_mins;\n");
        sb.Append("uniform vec3 origin_maxs;\n");
        sb.Append("uniform vec3 origin_offset;\n");        // world-axis constant add (already rotated/summed by host where relative)
        sb.Append("uniform vec3 origin_jitter;\n");        // per-axis jitter radius ⊙ rvec
        sb.Append("uniform vec3 vel_mins;\n");
        sb.Append("uniform vec3 vel_maxs;\n");
        sb.Append("uniform float velocity_multiplier;\n");
        sb.Append("uniform vec3 velocity_offset;\n");      // velocityoffset + rotated relativevelocityoffset (host pre-sums)
        sb.Append("uniform vec3 velocity_jitter;\n");      // per-axis jitter radius ⊙ rvec (SAME rvec as origin — the correlated radial expansion)
        sb.Append("uniform vec3 emit_velocity;\n");        // the supplied emit velocity (scaled by velocity_multiplier)
        sb.Append("uniform vec2 size_range;\n");           // lhrandom(size_min, size_max), edge units
        sb.Append("uniform vec2 alpha_range;\n");          // lhrandom(alpha_min, alpha_max), 0..256
        sb.Append("uniform float alpha_fade;\n");          // alpha-units/sec
        sb.Append("uniform vec2 time_range;\n");           // lhrandom(time_min, time_max); <=0 ⇒ life from alpha
        sb.Append("uniform float gravity_mult;\n");        // multiplier on DP 800 qu/s² (negative floats up)
        sb.Append("uniform vec2 angle_range;\n");          // rotate base min..max (radians)
        sb.Append("uniform vec3 color0;\n");               // DP color[0] (linear 0..1)
        sb.Append("uniform vec3 color1;\n");               // DP color[1]
        if (f.AirFriction) sb.Append("uniform float air_friction;\n");
        if (f.Bounce) sb.Append("uniform float bounce;\n");
        if (f.Spark) sb.Append("uniform float stretch_factor;\n");
        if (f.SizeIncrease) sb.Append("uniform float size_increase;\n"); // edge units/sec (host pre-doubled half→edge)
        if (f.Spin) sb.Append("uniform vec2 spin_range;\n");             // rotate spin min..max (radians/sec)
        if (f.Curl) sb.Append("uniform float curl_strength;\n");        // curl-noise amplitude (units/sec)
        sb.Append('\n');

        // --- DP random helpers --------------------------------------------------------------------------
        AppendRandomHelpers(sb, f);

        // --- start(): DP spawn (cl_particles.c:1754-1781 spawn, :668-849 NewParticle) --------------------
        // start() is called by Godot exactly once per (re)spawn — there is no RESTART guard here (RESTART is a
        // process-only builtin). All spawn randomization happens unconditionally.
        sb.Append("void start() {\n");
        // Seed: Godot gives RANDOM_SEED + NUMBER; derive a stable per-particle base seed.
        sb.Append("    uint seed = hash(NUMBER ^ (RANDOM_SEED * 1664525u + 1013904223u));\n");
        // ONE uniform-ball sample shared by origin AND velocity jitter (the correlated radial expansion, §B.1).
        // DP uses a rejection-sampled unit ball (mathlib.h:119); the exact-uniform GPU equivalent is
        // r = cbrt(U) * unitdir, unitdir uniform on the sphere (cl_particles.c relies only on the distribution).
        sb.Append("    vec3 rvec = ball_random(seed);\n");
        // org = lhrandom(originmins..maxs per axis) + originoffset + originjitter⊙rvec   (cl_particles.c:1755-1773)
        sb.Append("    vec3 org;\n");
        sb.Append("    org.x = lhrandom(seed, origin_mins.x, origin_maxs.x);\n");
        sb.Append("    org.y = lhrandom(seed, origin_mins.y, origin_maxs.y);\n");
        sb.Append("    org.z = lhrandom(seed, origin_mins.z, origin_maxs.z);\n");
        sb.Append("    org += origin_offset + origin_jitter * rvec;\n");
        // vel = lhrandom(velmins..maxs)·velmult + velocityoffset + velocityjitter⊙rvec + emit_velocity·velmult
        //       (cl_particles.c:1745-1781; relativevelocityoffset already folded into velocity_offset by host).
        sb.Append("    vec3 vel;\n");
        sb.Append("    vel.x = lhrandom(seed, vel_mins.x, vel_maxs.x);\n");
        sb.Append("    vel.y = lhrandom(seed, vel_mins.y, vel_maxs.y);\n");
        sb.Append("    vel.z = lhrandom(seed, vel_mins.z, vel_maxs.z);\n");
        sb.Append("    vel = vel * velocity_multiplier + velocity_offset\n");
        sb.Append("          + velocity_jitter * rvec + emit_velocity * velocity_multiplier;\n");
        // Per-particle size/alpha/life (cl_particles.c:707,726-730). DP: life = time>0 ? lhrandom(t) :
        // alpha/alphafade (the alpha-fade-derived lifetime). NOTE: the §B.1 paraphrase writes
        // "alpha/min(1,alphafade)", but that yields a 256-second life for the common explosion spark
        // (alpha 256, alphafade 1300) — contradicting the visible ~0.2s. The PINNED host contract
        // EffectInfoEmitter.Lifetime() uses alpha/alphafade and clamps to [0.05, 6]s; we match it exactly so
        // the modern per-particle life agrees with the faithful per-emitter life (and clamp the same way so a
        // pathological alphafade can't strand a particle for minutes). alpha here is 0..256.
        sb.Append("    float p_size  = lhrandom(seed, size_range.x, size_range.y);\n");
        sb.Append("    float p_alpha = clamp(lhrandom(seed, alpha_range.x, alpha_range.y) / 256.0, 0.0, 1.0);\n");
        sb.Append("    float p_life;\n");
        sb.Append("    if (time_range.y > 0.0) { p_life = lhrandom(seed, time_range.x, time_range.y); }\n");
        sb.Append("    else { p_life = (max(1.0, p_alpha * 256.0)) / max(0.0001, alpha_fade); }\n");
        sb.Append("    p_life = clamp(p_life, 0.05, 6.0);\n");
        // color = random lerp DP color[0]..color[1] (cl_particles.c:726-768).
        sb.Append("    float cl = randf(seed);\n");
        sb.Append("    vec3 p_color = mix(color0, color1, cl);\n");
        // angle = lhrandom(rotate base range) (radians).
        sb.Append("    float p_angle = lhrandom(seed, angle_range.x, angle_range.y);\n\n");

        // Write spawn state. EMISSION_TRANSFORM places the emitter; org is emitter-local (we add the column).
        sb.Append("    TRANSFORM = EMISSION_TRANSFORM;\n");
        sb.Append("    TRANSFORM[3].xyz += org;\n");
        // Per-particle billboard size lives in the basis column lengths (the draw shader reads MODEL_MATRIX
        // column length, exactly like the growth shader). Scale the 3x3 to p_size.
        sb.Append("    TRANSFORM[0].xyz = normalize(TRANSFORM[0].xyz) * p_size;\n");
        sb.Append("    TRANSFORM[1].xyz = normalize(TRANSFORM[1].xyz) * p_size;\n");
        sb.Append("    TRANSFORM[2].xyz = normalize(TRANSFORM[2].xyz) * p_size;\n");
        sb.Append("    VELOCITY = vel;\n");
        sb.Append("    COLOR = vec4(p_color, p_alpha);\n");
        // CUSTOM: x=angle, y=age(0 at birth), z=spawn alpha, w=per-particle life (see header allocation).
        sb.Append("    CUSTOM = vec4(p_angle, 0.0, p_alpha, p_life);\n");
        sb.Append("}\n\n");

        // --- process(): DP integration order (cl_particles.c:2958-3105) + §B.2 SDF response ---------------
        sb.Append("void process() {\n");
        // process() forbids `return` (Godot particles) — guard the whole body instead. RESTART is the spawn
        // frame (owned by start()); !ACTIVE is a free/dead slot. The kill-conditions inside set ACTIVE=false
        // and fall through harmlessly (a dead particle isn't drawn).
        sb.Append("    if (!RESTART && ACTIVE) {\n");
        sb.Append("    float dt = DELTA;\n");
        sb.Append("    float life = CUSTOM.w;\n");
        // No AGE builtin in particles shaders — track it ourselves in CUSTOM.y (seconds since spawn). DP kills on
        // die<=time (age>=life) OR alpha<=0.
        sb.Append("    CUSTOM.y += dt;\n");
        sb.Append("    float age = CUSTOM.y;\n\n");

        if (f.Curl)
        {
            // Modern curl-noise turbulence: a divergence-free swirl added to velocity (NOT a DP behaviour; a
            // §B.3 preset feature). Cheap gradient-noise curl about the particle position over time.
            sb.Append("    VELOCITY += curl3(TRANSFORM[3].xyz * 0.02 + vec3(TIME * 0.3)) * curl_strength * dt;\n");
        }

        // (1) VELOCITY.y -= gravity_mult * 800 * dt   (Godot Y is up; DP applies gravity to vel.z, the up axis).
        sb.Append("    VELOCITY.y -= gravity_mult * ");
        sb.Append(DpGravity.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture));
        sb.Append(" * dt;\n");

        // (2) airfriction AFTER gravity: VELOCITY *= 1 - min(airfriction*dt, 1)  (cl_particles.c:2972-2976).
        if (f.AirFriction)
            sb.Append("    VELOCITY *= 1.0 - min(air_friction * dt, 1.0);\n");

        // (3) integrate position ourselves (semi-implicit: velocity already updated this frame).
        sb.Append("    TRANSFORM[3].xyz += VELOCITY * dt;\n\n");

        // (4) alpha fade: COLOR.a = max(0, (alpha0 - alphafade*age)/256). alpha0 stashed (scaled) in CUSTOM.z.
        //     CUSTOM.z holds spawn alpha already /256, so reconstruct alpha0(0..256) = CUSTOM.z*256.
        sb.Append("    COLOR.a = max(0.0, (CUSTOM.z * 256.0 - alpha_fade * age) / 256.0);\n");

        // (5) deactivate on alpha<=0 || age>=life  (cl_particles.c:2966-2970 + die<=time).
        sb.Append("    if (COLOR.a <= 0.0 || age >= life) { ACTIVE = false; }\n\n");

        // Spin integration (rotate spin) — advance the stashed angle. CUSTOM.x is the live angle the draw reads.
        if (f.Spin)
            sb.Append("    CUSTOM.x += mix(spin_range.x, spin_range.y, randf_static(NUMBER)) * dt;\n");

        // (CUSTOM.y already holds the live age; the draw shader derives the lifetime phase = age/life itself.)

        // sizeincrease: grow/shrink the billboard basis over AGE (cl_particles.c size += sizeincrease*dt). We
        // scale the 3x3 columns so the draw shader (which reads column length) sees the live size. Edge units.
        if (f.SizeIncrease)
        {
            sb.Append("    {\n");
            sb.Append("        float curlen = length(TRANSFORM[0].xyz);\n");
            sb.Append("        float newlen = max(0.001, curlen + size_increase * dt);\n");
            sb.Append("        float s = newlen / max(1e-4, curlen);\n");
            sb.Append("        TRANSFORM[0].xyz *= s; TRANSFORM[1].xyz *= s; TRANSFORM[2].xyz *= s;\n");
            sb.Append("    }\n");
        }

        // --- §B.2 SDF collision response (COLLIDED / COLLISION_NORMAL / COLLISION_DEPTH) -----------------
        if (f.Bounce)
        {
            sb.Append("    if (COLLIDED) {\n");
            // push out by COLLISION_DEPTH along the normal (de-penetrate).
            sb.Append("        TRANSFORM[3].xyz += COLLISION_NORMAL * COLLISION_DEPTH;\n");
            sb.Append("        float vn = dot(VELOCITY, COLLISION_NORMAL);\n");
            if (f.BloodSplat)
            {
                // bounce < 0 (blood): remove + EMIT a splat sub-emitter oriented to the normal (§B.2). The
                // sub-emitter inherits via FLAG_EMIT_* below; we orient its +Z to the surface normal.
                sb.Append("        if (bounce < 0.0) {\n");
                sb.Append("            mat4 splat = mat4(1.0);\n");
                sb.Append("            vec3 n = COLLISION_NORMAL;\n");
                sb.Append("            vec3 t = normalize(abs(n.y) < 0.99 ? cross(n, vec3(0.0,1.0,0.0)) : cross(n, vec3(1.0,0.0,0.0)));\n");
                sb.Append("            vec3 b = cross(n, t);\n");
                sb.Append("            splat[0].xyz = t; splat[1].xyz = b; splat[2].xyz = n;\n");
                sb.Append("            splat[3].xyz = TRANSFORM[3].xyz + n * 0.5;\n");
                // emit_subparticle (the only custom function in particles shaders) spawns the splat into this
                // emitter's sub-emitter; flags select which fields it inherits. (§B.2; the persistent decal stays
                // CPU/faithful-side.) Returns a bool we don't need.
                sb.Append("            emit_subparticle(splat, vec3(0.0), COLOR, CUSTOM, FLAG_EMIT_POSITION | FLAG_EMIT_ROT_SCALE | FLAG_EMIT_COLOR | FLAG_EMIT_CUSTOM);\n");
                sb.Append("            ACTIVE = false;\n");
                sb.Append("        }\n");
            }
            // bounce > 0: DP semantics — VELOCITY += n * dot(VELOCITY,n) * (-bounce). (1=kill normal comp, 2=mirror.)
            // NOT Godot restitution. (cl_particles.c:3051-3052.)
            sb.Append("        VELOCITY += COLLISION_NORMAL * vn * (-bounce);\n");
            sb.Append("    }\n\n");
        }

        // --- slow-kill (cl_particles.c:3057-3062): dot(vel,vel) < 0.03 -> spark dies, others freeze ------
        if (f.Spark)
            sb.Append("    if (dot(VELOCITY, VELOCITY) < 0.03) { ACTIVE = false; }\n");
        else
            sb.Append("    if (dot(VELOCITY, VELOCITY) < 0.03) { VELOCITY = vec3(0.0); }\n");

        // --- spark live stretch (cl_particles.c:2812-2820): align the basis to current velocity, half-length
        //     max(stretch*0.04*|vel|, size*0.5), width = size. Streaks shorten as they decelerate. -----------
        if (f.Spark)
        {
            sb.Append("    {\n");
            sb.Append("        float spd = length(VELOCITY);\n");
            sb.Append("        if (spd > 1e-3) {\n");
            // Read the WIDTH from column 0 (stays = the spark's edge size every frame); column 1 is the stretched
            // length axis we overwrite below, so reading size from it would feed the prior frame's length back in.
            sb.Append("            float sz = length(TRANSFORM[0].xyz);\n");
            sb.Append("            float half_len = max(stretch_factor * 0.04 * spd, sz * 0.5);\n");
            sb.Append("            vec3 dir = VELOCITY / spd;\n");
            // Build an orthonormal basis with +Y(column 1) along velocity (the stretch axis), columns scaled
            // to (width=sz, length=2*half_len, width=sz). The draw shader treats column lengths as the quad size.
            sb.Append("            vec3 up0 = abs(dir.y) < 0.99 ? vec3(0.0,1.0,0.0) : vec3(1.0,0.0,0.0);\n");
            sb.Append("            vec3 sx = normalize(cross(up0, dir));\n");
            sb.Append("            vec3 sz3 = normalize(cross(dir, sx));\n");
            sb.Append("            TRANSFORM[0].xyz = sx * sz;\n");
            sb.Append("            TRANSFORM[1].xyz = dir * (2.0 * half_len);\n");
            sb.Append("            TRANSFORM[2].xyz = sz3 * sz;\n");
            sb.Append("        }\n");
            sb.Append("    }\n");
        }

        // --- snow flutter (cl_particles.c:3091-3098), INCLUDING DP's vel.y-from-vel.x bug (the reference look).
        if (f.Snow)
        {
            sb.Append("    {\n");
            // DP: every (rand()&3)*0.1s re-wander; we approximate with a time-hashed gate per particle.
            sb.Append("        uint hs = hash(NUMBER ^ uint(int(TIME * 7.0)));\n");
            sb.Append("        if ((hs & 3u) == 0u) {\n");
            sb.Append("            VELOCITY.x = VELOCITY.x * 0.9 + lhrandom_h(hs, -32.0, 32.0);\n");
            // DP BUG (faithfully replicated): vel.y is set from vel.X*0.9, not vel.Y.
            sb.Append("            VELOCITY.y = VELOCITY.x * 0.9 + lhrandom_h(hs, -32.0, 32.0);\n");
            sb.Append("        }\n");
            sb.Append("    }\n");
        }

        sb.Append("    }\n");   // close the (!RESTART && ACTIVE) guard
        sb.Append("}\n");
        return sb.ToString();
    }

    /// <summary>DP RNG helpers in GLSL: a small integer hash, lhrandom (mathlib.h:48) over a mutable seed, a
    /// uniform-ball sample (the exact-uniform cbrt form of DP's rejection ball, mathlib.h:119), and a curl-noise
    /// helper when needed. The seed is threaded by reference (mutated each draw) so sequential lhrandom calls in
    /// start() consume distinct values, matching DP's sequential rand() consumption order.</summary>
    private static void AppendRandomHelpers(StringBuilder sb, in ProcessFeatures f)
    {
        // PCG-style hash → 32-bit.
        sb.Append("uint hash(uint x) { x ^= x >> 16u; x *= 0x7feb352du; x ^= x >> 15u; x *= 0x846ca68bu; x ^= x >> 16u; return x; }\n");
        // advance seed, return 0..1 (inclusive-ish, DP uses (rand+0.5)/(RAND_MAX+1)).
        sb.Append("float randf(inout uint s) { s = hash(s); return (float(s & 0x00FFFFFFu) + 0.5) / 16777216.0; }\n");
        // static (non-threaded) variant keyed by an int, for per-particle constants reused in process().
        sb.Append("float randf_static(uint n) { uint s = hash(n * 2654435761u + 12345u); return (float(s & 0x00FFFFFFu) + 0.5) / 16777216.0; }\n");
        // lhrandom(min,max) = ((rand+0.5)/(RAND_MAX+1))*(max-min)+min  (mathlib.h:48).
        sb.Append("float lhrandom(inout uint s, float lo, float hi) { return randf(s) * (hi - lo) + lo; }\n");
        // hashed (non-inout) lhrandom for the snow flutter (consumes a one-off hash).
        if (f.Snow)
            sb.Append("float lhrandom_h(uint s, float lo, float hi) { return ((float(hash(s) & 0x00FFFFFFu) + 0.5) / 16777216.0) * (hi - lo) + lo; }\n");
        // uniform unit-ball: r = cbrt(U) * unitdir; unitdir from two uniforms (azimuth + cos-elevation).
        sb.Append("vec3 ball_random(inout uint s) {\n");
        sb.Append("    float u = randf(s);\n");
        sb.Append("    float r = pow(u, 1.0/3.0);\n");
        sb.Append("    float z = randf(s) * 2.0 - 1.0;\n");
        sb.Append("    float a = randf(s) * 6.2831853;\n");
        sb.Append("    float sr = sqrt(max(0.0, 1.0 - z*z));\n");
        sb.Append("    return vec3(sr * cos(a), sr * sin(a), z) * r;\n");
        sb.Append("}\n");
        if (f.Curl)
        {
            // Cheap value-noise gradient → curl (divergence-free). Not DP; a modern turbulence preset.
            sb.Append("float vnoise(vec3 p){ vec3 i=floor(p); vec3 fp=fract(p); fp=fp*fp*(3.0-2.0*fp);\n");
            sb.Append("    float n=dot(i,vec3(1.0,57.0,113.0));\n");
            sb.Append("    float a=fract(sin(n)*43758.5453); float b=fract(sin(n+1.0)*43758.5453);\n");
            sb.Append("    float c=fract(sin(n+57.0)*43758.5453); float d=fract(sin(n+58.0)*43758.5453);\n");
            sb.Append("    float e=fract(sin(n+113.0)*43758.5453); float g=fract(sin(n+114.0)*43758.5453);\n");
            sb.Append("    float h=fract(sin(n+170.0)*43758.5453); float k=fract(sin(n+171.0)*43758.5453);\n");
            sb.Append("    float x1=mix(mix(a,b,fp.x),mix(c,d,fp.x),fp.y);\n");
            sb.Append("    float x2=mix(mix(e,g,fp.x),mix(h,k,fp.x),fp.y);\n");
            sb.Append("    return mix(x1,x2,fp.z); }\n");
            sb.Append("vec3 curl3(vec3 p){ float e=0.1;\n");
            sb.Append("    float dx=vnoise(p+vec3(e,0,0))-vnoise(p-vec3(e,0,0));\n");
            sb.Append("    float dy=vnoise(p+vec3(0,e,0))-vnoise(p-vec3(0,e,0));\n");
            sb.Append("    float dz=vnoise(p+vec3(0,0,e))-vnoise(p-vec3(0,0,e));\n");
            sb.Append("    return vec3(dy-dz, dz-dx, dx-dy) / (2.0*e); }\n");
        }
        sb.Append('\n');
    }

    // =================================================================================================
    //  (b) DRAW SHADER — shader_type spatial (extends the growth shader)
    // =================================================================================================

    private static string BuildDrawShader(in DrawFeatures f)
    {
        var sb = new StringBuilder(3072);

        string blendMode = f.Blend switch
        {
            ModernBlend.Add => "blend_add",
            ModernBlend.Sub => "blend_sub",
            _ => "blend_mix",
        };

        sb.Append("shader_type spatial;\n");
        // Unshaded by default (energy/fire reads at full emissive); LIT path drops 'unshaded' so smoke shades.
        // depth_draw_opaque + depth-test ON keeps walls occluding the billboard (the growth-shader fix), and
        // depth_prepass_alpha is avoided (transparent particles must not write depth).
        sb.Append("render_mode ");
        sb.Append(blendMode);
        sb.Append(", cull_disabled, shadows_disabled, depth_draw_opaque");
        if (!f.Lit)
            sb.Append(", unshaded");
        sb.Append(";\n");

        sb.Append("uniform sampler2D albedo_tex : source_color, filter_linear;\n");
        if (f.Flipbook)
        {
            sb.Append("uniform int flip_hframes = 1;\n");
            sb.Append("uniform int flip_vframes = 1;\n");
            // INSTANCE_CUSTOM is a VERTEX-only builtin — carry the lifetime phase to fragment() via a varying.
            sb.Append("varying flat float v_phase;\n");
        }
        if (f.EmissiveBoost)
            sb.Append("uniform float emissive_boost = 1.0;\n");
        if (f.Soft)
        {
            sb.Append("uniform float soft_distance = 24.0;\n"); // world units of depth fade
            // DEPTH_TEXTURE was removed in Godot 4.x — sample the scene depth via a hint_depth_texture uniform.
            sb.Append("uniform sampler2D depth_tex : hint_depth_texture, repeat_disable, filter_nearest;\n");
        }
        sb.Append('\n');

        // ---- vertex(): billboard using the per-particle basis (column lengths = size), rotated by CUSTOM.x.
        // For SPARKS the process shader already wrote a velocity-aligned, length-stretched basis into the model
        // matrix, so we use it directly (no camera billboard) — exactly the DP spark streak.
        sb.Append("void vertex() {\n");
        if (f.Flipbook)
            // The process shader stores AGE in INSTANCE_CUSTOM.y and per-particle LIFE in .w (vertex-only).
            sb.Append("    v_phase = clamp(INSTANCE_CUSTOM.y / max(1e-4, INSTANCE_CUSTOM.w), 0.0, 1.0);\n");
        if (f.Spark)
        {
            // Use the model basis as-is, only orienting it to face the camera about the stretch (Y) axis so the
            // streak stays visible from any angle (Godot ZBillboardYToVelocity equivalent done in the basis).
            sb.Append("    MODELVIEW_MATRIX = VIEW_MATRIX * MODEL_MATRIX;\n");
        }
        else
        {
            // Per-instance custom data is INSTANCE_CUSTOM in a spatial shader (NOT CUSTOM, which is the particles
            // shader's name) — same convention as EffectSystem's growth shader. .x = the live rotation angle.
            sb.Append("    float angle = INSTANCE_CUSTOM.x;\n");
            sb.Append("    vec3 r = normalize(INV_VIEW_MATRIX[0].xyz) * length(MODEL_MATRIX[0].xyz);\n");
            sb.Append("    vec3 u = normalize(INV_VIEW_MATRIX[1].xyz) * length(MODEL_MATRIX[1].xyz);\n");
            sb.Append("    vec3 fn = normalize(INV_VIEW_MATRIX[2].xyz);\n");
            sb.Append("    mat4 bb = mat4(vec4(r, 0.0), vec4(u, 0.0), vec4(fn, 0.0), MODEL_MATRIX[3]);\n");
            sb.Append("    mat4 rot = mat4(vec4(cos(angle), -sin(angle), 0.0, 0.0), vec4(sin(angle), cos(angle), 0.0, 0.0), vec4(0.0, 0.0, 1.0, 0.0), vec4(0.0, 0.0, 0.0, 1.0));\n");
            sb.Append("    MODELVIEW_MATRIX = VIEW_MATRIX * (bb * rot);\n");
        }
        sb.Append("}\n\n");

        // ---- fragment(): atlas sample (with optional flipbook cell), blend output, soft-particle depth fade,
        //      emissive boost. Lit path writes ALBEDO+EMISSION; unshaded writes ALBEDO directly.
        sb.Append("void fragment() {\n");
        sb.Append("    vec2 uv = UV;\n");
        if (f.Flipbook)
        {
            // Advance the atlas cell by the lifetime phase across an hframes×vframes strip (phase from vertex).
            sb.Append("    float phase = v_phase;\n");
            sb.Append("    int frames = max(1, flip_hframes * flip_vframes);\n");
            sb.Append("    int cell = clamp(int(phase * float(frames)), 0, frames - 1);\n");
            sb.Append("    int cx = cell % max(1, flip_hframes);\n");
            sb.Append("    int cy = cell / max(1, flip_hframes);\n");
            sb.Append("    vec2 cs = vec2(1.0 / float(max(1, flip_hframes)), 1.0 / float(max(1, flip_vframes)));\n");
            sb.Append("    uv = (vec2(float(cx), float(cy)) + UV) * cs;\n");
        }
        sb.Append("    vec4 t = texture(albedo_tex, uv);\n");
        sb.Append("    float a = t.a * COLOR.a;\n");

        if (f.Soft)
        {
            // Soft particles: fade alpha as the billboard approaches the opaque depth behind it (no hard seam).
            // Sample DEPTH_TEXTURE, reconstruct linear view-space depth, compare to this fragment's depth.
            sb.Append("    float scene_d = texture(depth_tex, SCREEN_UV).r;\n");
            sb.Append("    vec3 ndc = vec3(SCREEN_UV * 2.0 - 1.0, scene_d);\n");
            sb.Append("    vec4 vp = INV_PROJECTION_MATRIX * vec4(ndc, 1.0);\n");
            sb.Append("    float scene_view_z = -(vp.z / vp.w);\n");          // positive distance in front of camera
            sb.Append("    float frag_view_z = -VERTEX.z;\n");                // VERTEX is in view space here
            sb.Append("    float fade = clamp((scene_view_z - frag_view_z) / max(0.001, soft_distance), 0.0, 1.0);\n");
            sb.Append("    a *= fade;\n");
        }

        if (f.Lit)
        {
            // Lit smoke: albedo is the tinted sprite; emission carries a (boosted) self-glow so it still reads in
            // shadow. The scene lights then modulate ALBEDO via the standard lit pipeline.
            sb.Append("    ALBEDO = t.rgb * COLOR.rgb;\n");
            if (f.EmissiveBoost)
                sb.Append("    EMISSION = t.rgb * COLOR.rgb * emissive_boost;\n");
            sb.Append("    ALPHA = a;\n");
        }
        else
        {
            // Unshaded (the faithful additive/energy look). EmissiveBoost scales the color into HDR for bloom.
            if (f.EmissiveBoost)
                sb.Append("    ALBEDO = t.rgb * COLOR.rgb * emissive_boost;\n");
            else
                sb.Append("    ALBEDO = t.rgb * COLOR.rgb;\n");
            sb.Append("    ALPHA = a;\n");
        }
        sb.Append("}\n");
        return sb.ToString();
    }
}

/// <summary>
/// The flattened, Godot-space spawn/draw parameters one modern emitter feeds into the shaders — the bridge
/// between the parsed <c>EffectInfoEmitter</c> (Quake space) and the shader uniforms. The backend
/// (<see cref="ModernParticleBackend"/>) fills this from a block (converting axes/units), then it both keys
/// the feature shapes and supplies the per-uniform values. Kept here (next to the shaders) so the uniform
/// names and this struct stay in lockstep.
/// </summary>
public struct ModernEmitterParams
{
    // physics
    public float AirFriction;
    public float Bounce;
    public float SizeIncrease;   // edge units/sec (already half→edge doubled)
    public float SpinMin, SpinMax;
    public float StretchFactor;

    // type/orientation flags driving feature shape
    public bool IsSpark;
    public bool IsSnow;

    // draw blend
    public ModernParticleShaders.ModernBlend Blend;
}
