using System;
using System.Collections.Generic;
using System.Numerics;
using XonoticGodot.Common.Framework;
using XonoticGodot.Common.Services;
using XonoticGodot.Engine.Collision;

namespace XonoticGodot.Engine.Particles;

// =====================================================================================================
//  FAITHFUL CPU particle simulation — the perfect-parity port of Darkplaces' cl_particles.c (spawn:
//  CL_NewParticle / CL_NewParticlesFromEffectinfo; update: R_DrawParticles integration loop). Pure,
//  Godot-free, headless-testable: cvars come through the injected Cvars store (ambient Api.Cvars fallback)
//  and collisions through the injected Trace seam (ambient Api.Trace fallback) — in the game both are wired
//  to the CLIENT stores (Cvars -> MenuState.Cvars, Trace -> a static world-only TraceService over the map's
//  client collision world), while tests leave them null and replay against the tools/particles-ref C
//  reference via the ambient facade. Randomness comes through the injected IParticleRng. See
//  planning/particles-dual-system.md §C.
//
//  PORT NOTES (DP source line numbers are cl_particles.c unless stated):
//   * lhrandom / VectorRandom reductions live in ParticleRandom (IParticleRng.cs) and are consumed in the
//     SAME ORDER as the C reference so RecordedParticleRng replays bit-for-bit.
//   * DP's ptype_t has pt_dead==0 as the free-slot sentinel (cl_particles.h:59), so liveness in DP is
//     "typeindex != 0". Our ParticleType enum has NO Dead member (AlphaStatic==0), so liveness is the
//     explicit Particle.Active flag instead. The free/high-water bookkeeping (free_particle/num_particles)
//     is reproduced against Active.
// =====================================================================================================

/// <summary>
/// A renderer/host hook for a stain or decal the sim wants drawn (DP R_Stain / CL_SpawnDecalParticleForSurface,
/// and CL_ImmediateBloodStain). The core sim stays pure — it never touches Godot — and instead raises this
/// when a particle hits a surface (or an immediate blood stain is requested at spawn). Org/vel/size/alpha of
/// the simulated particles are unaffected by whether a listener is attached.
///
/// COLOR CONTRACT: ColorR/G/B is the PERCEIVED mark color (what the wall should look like where the mark
/// is). DP's decals render INVMOD (wall · (1 − tex·color), the color being the amount REMOVED), with the
/// complement applied inconsistently per path — staintex stains pre-complement (cl_particles.c:3001
/// <c>0xFFFFFF ^ staincolor</c>, so staincolor is already perceived), while pt_decal and blood decals pass
/// the raw removal color. Each raise site below converts so the event always carries the perceived color;
/// the host feeds it to an alpha-blend decal directly.
/// </summary>
public readonly struct StainEvent
{
    public readonly Vector3 Org;        // world hit point
    public readonly Vector3 Dir;        // surface normal (or normalized velocity for blood smears)
    public readonly byte ColorR, ColorG, ColorB;
    public readonly float Size;
    public readonly float Alpha;        // 0..255-ish (DP stainalpha*stainsize scaling left to the host)
    public readonly int TexNum;         // staintexnum (>=0), or a blood-decal tex; <0 = engine picks
    public readonly bool IsBlood;
    public readonly bool Projected;     // true = no precomputed hit surface (immediate stain) -> host raycasts
    public readonly float MaxDist;      // projected-form ray reach (DP originjitter[0]); 0 = host default

    public StainEvent(Vector3 org, Vector3 dir, byte r, byte g, byte b, float size, float alpha, int texNum,
        bool isBlood, bool projected = false, float maxDist = 0f)
    {
        Org = org; Dir = dir; ColorR = r; ColorG = g; ColorB = b; Size = size; Alpha = alpha; TexNum = texNum;
        IsBlood = isBlood; Projected = projected; MaxDist = maxDist;
    }
}

/// <summary>The faithful CPU particle pool + simulation (DP cl_particles.c). One instance per backend.</summary>
public sealed class ParticleSim
{
    private Particle[] _pool;
    private int _highWater;        // scan upper bound — mirrors DP's cl.num_particles
    private int _freeParticle;     // low-water free slot — mirrors DP's cl.free_particle
    private readonly int _maxParticles;

    // Sim clock state — DP cl.particles_updatetime (advanced clamped each Update). Starts at 0 like DP's
    // cl.particles_updatetime at level start; the clamp to [time-1, time+1] bounds the first frametime to <=1.
    private float _updateTime;

    // The single sim clock (DP cl.time) — the time both spawn (die = now + lifetime) and update (frametime)
    // read. Driven by Update(time): the host advances it with render delta (a CLIENT visual clock, like the
    // GPU particles), NOT the server sim clock (which can be 0/paused on the render side). Spawns between
    // frames read the latest value — one-frame granularity, exactly as DP shares cl.time.
    private float _currentTime;

    /// <summary>The sim's current clock (last value passed to <see cref="Update"/>). Spawns use it for `die`.</summary>
    public float Now => _currentTime;

    // The default pool CEILING (the pool grows ×2 from initialCapacity up to this). 1<<16 = 65,536 keeps the
    // faithful sim near DP's practical particle count: the old 1<<18 = 262,144 gave BOTH the per-frame
    // collision-trace loop AND the full-capacity MultiMesh upload ~4× more headroom than DP ever uses, so a
    // heavy-combat burst could balloon both into the catastrophic particles.cpu hitch. Tests pass an explicit
    // max, so this only retunes the production backend (FaithfulParticleBackend uses the default).
    public ParticleSim(IParticleRng rng, int initialCapacity = 8192, int maxParticles = 1 << 16)
    {
        Rng = rng ?? throw new ArgumentNullException(nameof(rng));
        _maxParticles = Math.Max(initialCapacity, maxParticles);
        _pool = new Particle[Math.Max(64, initialCapacity)];
    }

    /// <summary>The randomness seam (xorshift in game, recorded sequence in tests).</summary>
    public IParticleRng Rng { get; }

    /// <summary>The particle pool. Live particles have <c>Active == true</c>; scan <c>[0, HighWater)</c>.</summary>
    public Particle[] Pool => _pool;

    /// <summary>Upper bound for live-particle scans (DP cl.num_particles). Grows as slots are used.</summary>
    public int HighWater => _highWater;

    /// <summary>Current pool array length (grows ×2 up to maxParticles when full, DP cl_particles.c:3174).</summary>
    public int Capacity => _pool.Length;

    /// <summary>Approximate live count (maintained by spawn/update for stats/HUD).</summary>
    public int LiveCount { get; private set; }

    /// <summary>Renderer hook for stains/decals raised on surface impact (and immediate blood stains).</summary>
    public Action<StainEvent>? OnStain;

    /// <summary>
    /// The CLIENT cvar store for the cl_particles* gates/quality. MUST be set to the client store
    /// (game: MenuState.Cvars) — on a listen server the ambient <c>Api.Cvars</c> is the SERVER store, which
    /// does NOT carry the client particle cvars, so cl_particles reads 0 and every spawn is gated off. Null
    /// (tests) falls back to <c>Api.Cvars</c>, which tests populate via Api.Services.
    /// </summary>
    public ICvarService? Cvars { get; set; }

    /// <summary>
    /// The collision/trace seam for particle bounces and content checks. Set this to the CLIENT's static,
    /// world-only tracer — a <see cref="XonoticGodot.Engine.Collision.TraceService"/> over the per-map
    /// <c>MapLoader.BuildCollision</c> world with NO entity provider and NO concurrency gate. That mirrors
    /// DP's <c>CL_TraceLine</c>: particles clip against the static map BSP only, never live players/items/
    /// projectiles, and never the SERVER's collision world. Null (tests) falls back to the ambient
    /// <c>Api.Trace</c> — which on a listen server IS the server world, whose per-trace cost AND tick
    /// serialisation gate made the bounce loop the dominant combat-frame hitch (see <see cref="Update"/>).
    /// </summary>
    public ITraceService? Trace { get; set; }

    private float Cv(string name) => (Cvars ?? Api.Cvars).GetFloat(name);
    private bool CvBool(string name) => (Cvars ?? Api.Cvars).GetFloat(name) != 0f;

    /// <summary>The resolved tracer: the injected client world-only tracer, or the ambient fallback (tests).</summary>
    private ITraceService TraceSvc => Trace ?? Api.Trace;

    // -----------------------------------------------------------------------------------------------------
    //  Spawn — CL_NewParticlesFromEffectinfo (1569-1788), non-trail/non-beam path.
    // -----------------------------------------------------------------------------------------------------

    /// <summary>
    /// CL_NewParticlesFromEffectinfo (cl_particles.c:1569-1788): spawn every block of one effect (DP layers
    /// same-named blocks in file order). For a POINT effect pass originMins == originMaxs (the spawn center)
    /// and velocityMins == velocityMaxs; for a TRAIL pass the segment endpoints in origin{Mins,Maxs}. The
    /// shared per-block <see cref="ParticleEmitterInfo"/> accumulator is mutated to drain whole particles.
    /// </summary>
    /// <param name="pcount">DP <c>pcount</c> — the requested count; countmultiplier·quality applied inside.</param>
    /// <param name="tintRgba">Optional per-spawn tint (RGBA bytes, 0xFFFFFFFF = none); multiplies color/alpha.</param>
    /// <param name="fade">DP fade multiplier on the spawn count (distance/quality fade); 1 = none.</param>
    public void SpawnEffect(
        IReadOnlyList<ParticleEmitterInfo> blocks, float pcount,
        Vector3 originMins, Vector3 originMaxs,
        Vector3 velocityMins, Vector3 velocityMaxs,
        uint tintRgba = 0xFFFFFFFFu, float fade = 1f)
    {
        if (blocks == null || blocks.Count == 0) return;
        // cl_particles gate (1697) — also CL_NewParticle's own guard.
        if (!CvBool(ParticleCvars.Particles)) return;

        float now = _currentTime;   // the sim clock (set by Update), NOT Api.Clock — see _currentTime.
        float quality = Cv(ParticleCvars.Quality);

        // VectorLerp(originmins, 0.5, originmaxs, center) (1600).
        Vector3 center = originMins + (originMaxs - originMins) * 0.5f;
        int supercontents = TraceSvc.PointContents(center);
        bool underwater = (supercontents & (SuperContents.Water | SuperContents.Slime)) != 0;

        // traildir / traillen (1603-1605).
        Vector3 traildir = originMaxs - originMins;
        float traillen = traildir.Length();
        {
            float ilen = Vector3.Dot(traildir, traildir);    // DP VectorNormalize: zero -> stays zero
            if (ilen != 0f) traildir *= 1f / MathF.Sqrt(ilen);
        }

        // Tint (1606-1613). tintRgba 0xFFFFFFFF means "no tint" (pass null to CL_NewParticle path).
        bool hasTint = tintRgba != 0xFFFFFFFFu;
        Vector4 tintMin = default, tintMax = default;
        if (hasTint)
        {
            // We model a single tint (mins==maxs) from the RGBA bytes; the avg/lerp draws still match the C
            // when the C passes tintmins==NULL (the common case our goldens use). When a tint is supplied the
            // per-particle lhrandom(0,1) draw is consumed exactly as DP (1769).
            float r = ((tintRgba >> 24) & 0xFF) / 255f;
            float g = ((tintRgba >> 16) & 0xFF) / 255f;
            float b = ((tintRgba >> 8) & 0xFF) / 255f;
            float a = (tintRgba & 0xFF) / 255f;
            tintMin = tintMax = new Vector4(r, g, b, a);
        }

        foreach (ParticleEmitterInfo info in blocks)
        {
            bool definedAsTrail = info.TrailSpacing > 0f;
            bool drawAsTrail = false; // wanttrail: our SpawnEffect spawns a box/point; trail callers set TrailSpacing>0.
            // A trail block (TrailSpacing>0) draws as a trail from originmins..originmaxs.
            if (definedAsTrail) drawAsTrail = true;

            // Water gates (1625-1628).
            if (info.Underwater && !underwater) continue;
            if (info.NotUnderwater && underwater) continue;

            // Resolve tex (1661-1666) and staintex (1667-1673) — both drawn once per block before the loop.
            int tex = info.Tex0;
            if (info.Tex1 > info.Tex0)
            {
                tex = (int)ParticleRandom.Lhrandom(Rng, info.Tex0, info.Tex1);
                if (tex > info.Tex1 - 1) tex = info.Tex1 - 1;
            }
            int staintex;
            if (info.StainTex0 < 0) staintex = info.StainTex0;
            else
            {
                staintex = (int)ParticleRandom.Lhrandom(Rng, info.StainTex0, info.StainTex1);
                if (staintex > info.StainTex1 - 1) staintex = info.StainTex1 - 1;
            }

            // pt_decal / HBEAM paths are not modeled here (the box/point spawn path). Type gates (1699-1708):
            switch (info.Type)
            {
                case ParticleType.Smoke:  if (!CvBool(ParticleCvars.Smoke))   continue; break;
                case ParticleType.Spark:  if (!CvBool(ParticleCvars.Sparks))  continue; break;
                case ParticleType.Bubble: if (!CvBool(ParticleCvars.Bubbles)) continue; break;
                case ParticleType.Blood:  if (!CvBool(ParticleCvars.Blood))   continue; break;
                case ParticleType.Rain:   if (!CvBool(ParticleCvars.Rain))    continue; break;
                case ParticleType.Snow:   if (!CvBool(ParticleCvars.Snow))    continue; break;
                default: break;
            }

            // cnt (1710-1721).
            float cnt = info.CountAbsolute;
            cnt += (pcount * info.CountMultiplier) * quality;
            if (drawAsTrail && definedAsTrail)
                cnt += (traillen / info.TrailSpacing) * quality;
            cnt *= fade;
            if (cnt == 0f) continue;
            info.ParticleAccumulator += cnt;

            // immediatebloodstain (1723-1729). Decals only fire through OnStain; pure sim is unaffected.
            bool immediateBloodStain;
            if (drawAsTrail || definedAsTrail)
                immediateBloodStain = false;
            else
            {
                int ibs = (int)Cv(ParticleCvars.DecalsImmediateBloodStain);
                immediateBloodStain =
                    (ibs >= 1 && info.Type == ParticleType.Blood) ||
                    (ibs >= 2 && staintex != 0);
            }

            // trailpos / trailstep (1731-1740).
            Vector3 trailpos;
            float trailstep;
            if (drawAsTrail)
            {
                trailpos = originMins;
                trailstep = traillen / cnt;
            }
            else
            {
                trailpos = center;
                trailstep = 0f;
            }

            // basis (1742-1752).
            Vector3 angles, velocity, forward, right, up;
            if (trailstep == 0f)
            {
                velocity = velocityMins * 0.5f + velocityMaxs * 0.5f;   // VectorMAM(0.5,vmin,0.5,vmax)
                angles = AnglesFromVectorsNoUp(velocity);
            }
            else
            {
                angles = AnglesFromVectorsNoUp(traildir);
            }
            AngleVectors(angles, out forward, out right, out up);

            // VectorMAMAMAM(1, trailpos, rel[0],fwd, rel[1],right, rel[2],up, trailpos) (1751).
            trailpos += forward * info.RelativeOriginOffset.X
                      + right   * info.RelativeOriginOffset.Y
                      + up      * info.RelativeOriginOffset.Z;
            // VectorMAMAM(relvel[0],fwd, relvel[1],right, relvel[2],up, velocity) (1752).
            velocity = forward * info.RelativeVelocityOffset.X
                     + right   * info.RelativeVelocityOffset.Y
                     + up      * info.RelativeVelocityOffset.Z;

            // type decal — DP special-cases these BEFORE the count/accumulator path (:1674-1682): exactly
            // ONE surface splat per spawn call (count ignored), searched from the effect center (+ relative
            // offset, already folded into trailpos for point effects) with originjitter[0] as the ray reach
            // — the jitter is a SEARCH RADIUS, never a position offset. Draws in DP order: tex, size,
            // alpha·avgtint, then the color byte-lerp CL_SpawnDecalParticleForSurface performs (:969-973).
            // The color is the raw INVMOD removal amount (the splat system multiplies wall·(1−tex·color)).
            if (info.Type == ParticleType.Decal)
            {
                int dtex = info.Tex0;
                if (info.Tex1 > info.Tex0)
                {
                    dtex = (int)ParticleRandom.Lhrandom(Rng, info.Tex0, info.Tex1);
                    if (dtex > info.Tex1 - 1) dtex = info.Tex1 - 1;
                }
                float dsize = (float)ParticleRandom.Lhrandom(Rng, info.SizeMin, info.SizeMax);
                float dalpha = (float)ParticleRandom.Lhrandom(Rng, info.AlphaMin, info.AlphaMax);
                if (hasTint)
                    dalpha *= (tintMin.W + tintMax.W) * 0.5f;   // DP: ·avgtint[3]
                int dl2 = (int)ParticleRandom.Lhrandom(Rng, 0.5, 256.5);
                int dl1 = 256 - dl2;
                byte dr = (byte)((((int)((info.Color0 >> 16) & 0xFF) * dl1 + (int)((info.Color1 >> 16) & 0xFF) * dl2) >> 8) & 0xFF);
                byte dg = (byte)((((int)((info.Color0 >> 8) & 0xFF) * dl1 + (int)((info.Color1 >> 8) & 0xFF) * dl2) >> 8) & 0xFF);
                byte db = (byte)((((int)((info.Color0 >> 0) & 0xFF) * dl1 + (int)((info.Color1 >> 0) & 0xFF) * dl2) >> 8) & 0xFF);
                OnStain?.Invoke(new StainEvent(trailpos, default, dr, dg, db, dsize, dalpha, dtex,
                    isBlood: false, projected: true, maxDist: info.OriginJitter.X));
                continue;
            }

            // bound(0, acc, 16384) (1753).
            if (info.ParticleAccumulator < 0) info.ParticleAccumulator = 0;
            if (info.ParticleAccumulator > 16384) info.ParticleAccumulator = 16384;

            // drain whole particles (1754-1781).
            for (; info.ParticleAccumulator >= 1; info.ParticleAccumulator -= 1.0)
            {
                if (info.Tex1 > info.Tex0)
                {
                    tex = (int)ParticleRandom.Lhrandom(Rng, info.Tex0, info.Tex1);
                    if (tex > info.Tex1 - 1) tex = info.Tex1 - 1;
                }
                if (!(drawAsTrail || definedAsTrail))
                {
                    trailpos.X = (float)ParticleRandom.Lhrandom(Rng, originMins.X, originMaxs.X);
                    trailpos.Y = (float)ParticleRandom.Lhrandom(Rng, originMins.Y, originMaxs.Y);
                    trailpos.Z = (float)ParticleRandom.Lhrandom(Rng, originMins.Z, originMaxs.Z);
                }
                Vector4? tint = null;
                if (hasTint)
                {
                    float tl = (float)ParticleRandom.Lhrandom(Rng, 0, 1);
                    tint = tintMin + (tintMax - tintMin) * tl;
                }

                // ONE VectorRandom sample shared by origin AND velocity jitter (1772).
                Vector3 rvec = ParticleRandom.VectorRandom(Rng);

                float ox = trailpos.X + info.OriginOffset.X + info.OriginJitter.X * rvec.X;
                float oy = trailpos.Y + info.OriginOffset.Y + info.OriginJitter.Y * rvec.Y;
                float oz = trailpos.Z + info.OriginOffset.Z + info.OriginJitter.Z * rvec.Z;
                float vx = (float)ParticleRandom.Lhrandom(Rng, velocityMins.X, velocityMaxs.X) * info.VelocityMultiplier + info.VelocityOffset.X + info.VelocityJitter.X * rvec.X + velocity.X;
                float vy = (float)ParticleRandom.Lhrandom(Rng, velocityMins.Y, velocityMaxs.Y) * info.VelocityMultiplier + info.VelocityOffset.Y + info.VelocityJitter.Y * rvec.Y + velocity.Y;
                float vz = (float)ParticleRandom.Lhrandom(Rng, velocityMins.Z, velocityMaxs.Z) * info.VelocityMultiplier + info.VelocityOffset.Z + info.VelocityJitter.Z * rvec.Z + velocity.Z;

                // Per-particle lhrandom draws, IN DP ORDER (1773): size, then size[2] is fixed, then alpha,
                // then alpha[2] fixed, ... time, stainalpha, stainsize, rotate base, rotate spin.
                float psize = (float)ParticleRandom.Lhrandom(Rng, info.SizeMin, info.SizeMax);
                float palpha = (float)ParticleRandom.Lhrandom(Rng, info.AlphaMin, info.AlphaMax);
                float lifetime = (float)ParticleRandom.Lhrandom(Rng, info.TimeMin, info.TimeMax);
                float pstainalpha = (float)ParticleRandom.Lhrandom(Rng, info.StainAlphaMin, info.StainAlphaMax);
                float pstainsize = (float)ParticleRandom.Lhrandom(Rng, info.StainSizeMin, info.StainSizeMax);
                float pangle = (float)ParticleRandom.Lhrandom(Rng, info.RotateBaseMin, info.RotateBaseMax);
                float pspin = (float)ParticleRandom.Lhrandom(Rng, info.RotateSpinMin, info.RotateSpinMax);

                int idx = NewParticle(now, info.Type, info.Color0, info.Color1, tex,
                    psize, info.SizeIncrease, palpha, info.AlphaFade,
                    info.Gravity, info.Bounce, ox, oy, oz, vx, vy, vz,
                    info.AirFriction, info.LiquidFriction,
                    lifetime, info.StretchFactor, info.Blend, info.Orientation,
                    (int)info.StainColor0, (int)info.StainColor1, staintex,
                    pstainalpha, pstainsize, pangle, pspin, tint);

                // DP passes the effect CENTER as every burst particle's sortorigin (1773 first arg) — the
                // whole burst sorts as one group and composites in pool/spawn order within it.
                if (idx >= 0)
                    _pool[idx].SortOrg = center;

                // CL_ImmediateBloodStain (:935-956): up to TWO splats along the particle's velocity — the
                // staintex mark with the COMPLEMENTED staincolor (1 − staincolor is the removal), and for
                // blood an additional blooddecal mark with the particle's RAW invmod color and 2× size.
                if (immediateBloodStain && idx >= 0)
                {
                    immediateBloodStain = false;
                    ref Particle p = ref _pool[idx];
                    if (p.StainTexNum >= 0)
                        OnStain?.Invoke(new StainEvent(p.Org, p.Vel,
                            (byte)(255 - p.StainColorR), (byte)(255 - p.StainColorG), (byte)(255 - p.StainColorB),
                            p.StainSize, p.StainAlpha, p.StainTexNum, isBlood: false, projected: false));
                    if (p.TypeIndex == ParticleType.Blood)
                        OnStain?.Invoke(new StainEvent(p.Org, p.Vel, p.ColorR, p.ColorG, p.ColorB,
                            p.Size * 2f, p.Alpha, -1, isBlood: true, projected: false));
                }

                if (trailstep != 0f) trailpos += traildir * trailstep;
            }
        }
    }

    // -----------------------------------------------------------------------------------------------------
    //  CL_NewParticle (cl_particles.c:668-849). Returns the pool index, or -1 if none allocated.
    // -----------------------------------------------------------------------------------------------------
    private int NewParticle(
        float now, ParticleType ptypeindex, uint pcolor1, uint pcolor2, int ptex,
        float psize, float psizeincrease, float palpha, float palphafade,
        float pgravity, float pbounce,
        float px, float py, float pz, float pvx, float pvy, float pvz,
        float pairfriction, float pliquidfriction,
        float lifetime, float stretch, ParticleBlend blendmode, ParticleOrientation orientation,
        int staincolor1, int staincolor2, int staintex,
        float stainalpha, float stainsize, float angle, float spin, Vector4? tint)
    {
        // cl_particles gate + free-slot scan (702-706).
        if (!CvBool(ParticleCvars.Particles)) return -1;
        while (_freeParticle < _maxParticles && _freeParticle < _pool.Length && _pool[_freeParticle].Active)
            _freeParticle++;
        if (_freeParticle >= _maxParticles)
            return -1;
        // grow ×2 up to maxParticles when the free slot runs past the array (DP grows post-update at 3174;
        // we grow on demand so a single spawn burst never overruns).
        if (_freeParticle >= _pool.Length)
            Grow();

        if (lifetime == 0f)
            lifetime = palpha / MathF.Min(1f, palphafade);   // (707-708)

        int idx = _freeParticle++;
        if (_highWater < _freeParticle) _highWater = _freeParticle;

        ref Particle p = ref _pool[idx];
        p = default;                 // memset(part, 0, ...) (712)
        p.Active = true;
        p.TypeIndex = ptypeindex;
        p.BlendMode = blendmode;
        p.Orientation = orientation; // beam HBEAM/VBEAM resolution skipped — not used by the box/point path

        // color byte-lerp (726-730).
        int l2 = (int)ParticleRandom.Lhrandom(Rng, 0.5, 256.5);
        int l1 = 256 - l2;
        p.ColorR = (byte)(((int)(((pcolor1 >> 16) & 0xFF) * l1 + ((pcolor2 >> 16) & 0xFF) * l2) >> 8) & 0xFF);
        p.ColorG = (byte)(((int)(((pcolor1 >> 8) & 0xFF) * l1 + ((pcolor2 >> 8) & 0xFF) * l2) >> 8) & 0xFF);
        p.ColorB = (byte)(((int)(((pcolor1 >> 0) & 0xFF) * l1 + ((pcolor2 >> 0) & 0xFF) * l2) >> 8) & 0xFF);

        p.Alpha = palpha;
        p.AlphaFade = palphafade;
        p.StainTexNum = (sbyte)staintex;

        int r, g, b;
        if (staincolor1 >= 0 && staincolor2 >= 0)
        {
            l2 = (int)ParticleRandom.Lhrandom(Rng, 0.5, 256.5);
            l1 = 256 - l2;
            if (blendmode == ParticleBlend.InvMod)
            {
                r = ((((staincolor1 >> 16) & 0xFF) * l1 + ((staincolor2 >> 16) & 0xFF) * l2) * (255 - p.ColorR)) / 0x8000;
                g = ((((staincolor1 >> 8) & 0xFF) * l1 + ((staincolor2 >> 8) & 0xFF) * l2) * (255 - p.ColorG)) / 0x8000;
                b = ((((staincolor1 >> 0) & 0xFF) * l1 + ((staincolor2 >> 0) & 0xFF) * l2) * (255 - p.ColorB)) / 0x8000;
            }
            else
            {
                r = ((((staincolor1 >> 16) & 0xFF) * l1 + ((staincolor2 >> 16) & 0xFF) * l2) * p.ColorR) / 0x8000;
                g = ((((staincolor1 >> 8) & 0xFF) * l1 + ((staincolor2 >> 8) & 0xFF) * l2) * p.ColorG) / 0x8000;
                b = ((((staincolor1 >> 0) & 0xFF) * l1 + ((staincolor2 >> 0) & 0xFF) * l2) * p.ColorB) / 0x8000;
            }
            if (r > 0xFF) r = 0xFF;
            if (g > 0xFF) g = 0xFF;
            if (b > 0xFF) b = 0xFF;
        }
        else
        {
            r = p.ColorR; g = p.ColorG; b = p.ColorB;   // -1 shorthand: stain = particle color (762-764)
        }
        p.StainColorR = (byte)r; p.StainColorG = (byte)g; p.StainColorB = (byte)b;
        p.StainAlpha = palpha * stainalpha;
        p.StainSize = psize * stainsize;

        if (tint.HasValue)
        {
            Vector4 t = tint.Value;
            if (blendmode != ParticleBlend.InvMod)
            {
                p.ColorR = (byte)(p.ColorR * t.X);
                p.ColorG = (byte)(p.ColorG * t.Y);
                p.ColorB = (byte)(p.ColorB * t.Z);
            }
            p.Alpha *= t.W;
            p.AlphaFade *= t.W;
            p.StainAlpha *= t.W;
        }

        p.TexNum = (byte)ptex;
        p.Size = psize;
        p.SizeIncrease = psizeincrease;
        p.Gravity = pgravity;
        p.Bounce = pbounce;
        p.Stretch = stretch;

        // VectorRandom(v) (789): one ball sample. In the effectinfo path originjitter/velocityjitter passed
        // to CL_NewParticle are 0 (the caller folded its own shared rvec into px/pvx already), so this only
        // consumes a draw — but we MUST consume it to stay in lockstep with the C reference.
        ParticleRandom.VectorRandom(Rng);
        p.Org = new Vector3(px, py, pz);
        p.Vel = new Vector3(pvx, pvy, pvz);
        p.SortOrg = p.Org;   // internal sub-spawns sort by their own org; SpawnEffect overrides to the effect center

        p.AirFriction = pairfriction;
        p.LiquidFriction = pliquidfriction;
        p.Die = now + lifetime;
        p.DelayedSpawn = now;
        p.Angle = angle;
        p.Spin = spin;

        // Rain -> traced spark + raindecal + delayed splash sparks (804-832). Best-effort; the sub-particles
        // consume rand() exactly so the trace stays aligned. Splash/decal visuals go through OnStain on the
        // renderer side if a host wants them; here we faithfully spawn the spark sub-particles DP creates.
        if (p.TypeIndex == ParticleType.Rain)
        {
            float gravityVar = SvGravity();
            // Re-read by ref because NewParticle calls below may grow/realloc the pool.
            Vector3 org = _pool[idx].Org, vel = _pool[idx].Vel;
            _pool[idx].TypeIndex = ParticleType.Spark;
            _pool[idx].Bounce = 0f;
            Vector3 endvec = org + vel * lifetime;
            TraceResult tr = TraceSvc.Trace(org, Vector3.Zero, Vector3.Zero, endvec,
                MoveFilter.NoMonsters, null);
            _pool[idx].Die = now + lifetime * tr.Fraction;
            Vector3 dorg = tr.EndPos + tr.PlaneNormal;
            int rd = NewParticle(now, ParticleType.RainDecal, pcolor1, pcolor2, 0, _pool[idx].Size, _pool[idx].Size * 20f,
                _pool[idx].Alpha, _pool[idx].Alpha / 0.4f, 0f, 0f,
                dorg.X, dorg.Y, dorg.Z, tr.PlaneNormal.X, tr.PlaneNormal.Y, tr.PlaneNormal.Z,
                0f, 0f, 1f, stretch, ParticleBlend.Add, ParticleOrientation.Oriented, -1, -1, -1, 1f, 1f, 0f, 0f, null);
            if (rd >= 0)
            {
                float pdie = _pool[idx].Die;
                _pool[rd].DelayedSpawn = pdie;
                _pool[rd].Die += pdie - now;
                for (int i = Rng.NextRaw() & 7; i < 10; i++)
                {
                    Vector3 sorg = tr.EndPos + tr.PlaneNormal;
                    Vector3 svel = new(tr.PlaneNormal.X * 16f, tr.PlaneNormal.Y * 16f, tr.PlaneNormal.Z * 16f + gravityVar * 0.04f);
                    int sp = NewParticle(now, ParticleType.Spark, pcolor1, pcolor2, 0, 0.25f, 0f, _pool[idx].Alpha * 2f, _pool[idx].Alpha * 4f,
                        1f, 0.1f, sorg.X, sorg.Y, sorg.Z, svel.X, svel.Y, svel.Z,
                        0f, 0f, 32f, stretch, ParticleBlend.Add, ParticleOrientation.Spark, -1, -1, -1, 1f, 1f, 0f, 0f, null);
                    if (sp >= 0)
                    {
                        _pool[sp].DelayedSpawn = pdie;
                        _pool[sp].Die += pdie - now;
                    }
                }
            }
        }

        return idx;
    }

    // -----------------------------------------------------------------------------------------------------
    //  Update — R_DrawParticles integration loop (cl_particles.c:2907-3132).
    // -----------------------------------------------------------------------------------------------------

    /// <summary>
    /// R_DrawParticles per-frame integration (cl_particles.c:2907-3105): advance every live particle by the
    /// clamped frametime derived from <paramref name="time"/> (the sim tracks its own updatetime) and publish
    /// it as <see cref="Now"/> so spawns this frame share the clock. In game pass an ACCUMULATING client
    /// render clock (sum of frame deltas) — NOT the server sim clock; in tests pass the scripted step time.
    /// </summary>
    public void Update(float time)
    {
        // frametime = bound(0, time - updatetime, 1); updatetime = bound(time-1, updatetime+frametime, time+1).
        // (cl_particles.c:2921-2922) — _updateTime starts at 0, so the first frametime is `time` clamped to 1.
        _currentTime = time;   // publish the sim clock so spawns this frame read the same `now`
        float frametime = time - _updateTime;
        if (frametime < 0f) frametime = 0f; else if (frametime > 1f) frametime = 1f;
        float lo = time - 1f, hi = time + 1f;
        float ut = _updateTime + frametime;
        if (ut < lo) ut = lo; else if (ut > hi) ut = hi;
        _updateTime = ut;

        if (_highWater == 0) return;

        bool collisions = CvBool(ParticleCvars.Collisions);
        float svGravity = SvGravity();
        float gravity = frametime * svGravity;
        bool update = frametime > 0f;
        int live = 0;

        for (int i = 0; i < _highWater; i++)
        {
            ref Particle p = ref _pool[i];
            if (!p.Active)
            {
                if (_freeParticle > i) _freeParticle = i;
                continue;
            }

            if (update)
            {
                if (p.DelayedSpawn > time) { live++; continue; }

                p.Size += p.SizeIncrease * frametime;
                p.Alpha -= p.AlphaFade * frametime;
                if (p.Alpha <= 0f || p.Die <= time) { Kill(ref p, i); continue; }

                // Beams skip physics (we have no V/HBEAM split; ParticleOrientation.Beam is the beam).
                if (p.Orientation != ParticleOrientation.Beam && frametime > 0f)
                {
                    float f;
                    bool inLiquid = p.LiquidFriction != 0f && collisions &&
                                    (TraceSvc.PointContents(p.Org) & SuperContents.LiquidsMask) != 0;
                    if (inLiquid)
                    {
                        if (p.TypeIndex == ParticleType.Blood) p.Size += frametime * 8f;
                        else p.Vel.Z -= p.Gravity * gravity;
                        f = 1f - MathF.Min(p.LiquidFriction * frametime, 1f);
                        p.Vel *= f;
                    }
                    else
                    {
                        p.Vel.Z -= p.Gravity * gravity;
                        if (p.AirFriction != 0f)
                        {
                            f = 1f - MathF.Min(p.AirFriction * frametime, 1f);
                            p.Vel *= f;
                        }
                    }

                    Vector3 oldorg = p.Org;
                    p.Org += p.Vel * frametime;

                    if (p.Bounce != 0f && collisions && p.Vel.Length() != 0f)
                    {
                        int hitmask = SuperContents.Solid |
                            ((p.TypeIndex == ParticleType.Rain || p.TypeIndex == ParticleType.Snow) ? SuperContents.LiquidsMask : 0);
                        // DP's per-frame bounce trace (cl_particles.c:2984) is CL_TraceLine(..., MOVE_NORMAL, ...,
                        // hitnetworkbrushmodels=true, hitnetworkplayers=false, hitcsqcentities=false) — it clips
                        // against the static world BSP, never players/items/monsters/projectiles. We route this
                        // through TraceSvc — the injected CLIENT world-only tracer (a TraceService over the per-map
                        // collision world, entities=null, no concurrency gate) — exactly DP's cl.worldmodel trace.
                        // The MoveFilter.NoMonsters argument is now redundant (an entities-null tracer has no entity
                        // pass) but kept so the fallback ambient Api.Trace stays faithful in tests. Routing this at
                        // the live SERVER world was the dominant combat-frame hitch: every bouncing spark/ember box-
                        // swept the entire live entity broadphase under the server-tick lock each frame; the static
                        // client tracer removes both the per-trace cost and the cross-thread serialisation.
                        TraceResult tr = TraceSvc.Trace(oldorg, Vector3.Zero, Vector3.Zero, p.Org, MoveFilter.NoMonsters, null);
                        // Honor only the requested hitmask: a SOLID-only particle ignores a liquid surface.
                        bool hitWanted = (tr.DpHitContents & hitmask) != 0 || tr.StartSolid;
                        if (tr.Fraction < 1f && !hitWanted)
                        {
                            // Trace stopped on a surface outside our hit mask — treat as a clean pass-through.
                            // (Our analytic test worlds only contain SOLID + a LIQUID box; the LIQUID box is
                            // never a SOLID trace stopper, so this matches the C reference's per-mask trace.)
                        }
                        else
                        {
                            int startContents = StartContents(tr);
                            if ((tr.DpHitQ3SurfaceFlags & Q3SurfaceFlags.NoImpact) != 0
                                || (((startContents | tr.DpHitContents) & SuperContents.NoDrop) != 0)
                                || ((startContents & SuperContents.Solid) != 0))
                            { Kill(ref p, i); continue; }

                            p.Org = tr.EndPos;
                            if (tr.Fraction < 1f)
                            {
                                p.Org = tr.EndPos;
                                // Decal projection axis (:3007-3012): the particle's velocity when
                                // cl_decals_newsystem_bloodsmears is on (smeared marks), else the hit normal.
                                Vector3 decaldir = CvBool(ParticleCvars.DecalsBloodSmears) && p.Vel.LengthSquared() > 1e-6f
                                    ? Vector3.Normalize(p.Vel)
                                    : tr.PlaneNormal;

                                if (p.StainTexNum >= 0 && (tr.DpHitQ3SurfaceFlags & Q3SurfaceFlags.NoMarks) == 0)
                                {
                                    // :2999: a = 0xFFFFFF ^ staincolor — the COMPLEMENT is the INVMOD removal.
                                    OnStain?.Invoke(new StainEvent(p.Org, decaldir,
                                        (byte)(255 - p.StainColorR), (byte)(255 - p.StainColorG), (byte)(255 - p.StainColorB),
                                        p.StainSize, p.StainAlpha, p.StainTexNum, p.TypeIndex == ParticleType.Blood));
                                }

                                if (p.TypeIndex == ParticleType.Blood)
                                {
                                    if ((tr.DpHitQ3SurfaceFlags & Q3SurfaceFlags.NoMarks) != 0) { Kill(ref p, i); continue; }
                                    if (p.StainTexNum == -1)
                                    {
                                        // :3033: the blood decal renders INVMOD on the PARTICLE's raw color
                                        // (the 0xA8FFFF family = "remove green/blue" → reads dark red).
                                        OnStain?.Invoke(new StainEvent(p.Org, decaldir,
                                            p.ColorR, p.ColorG, p.ColorB, p.Size, p.Alpha, -1, true));
                                    }
                                    Kill(ref p, i); continue;
                                }
                                else if (p.Bounce < 0f)
                                {
                                    Kill(ref p, i); continue;
                                }
                                else
                                {
                                    float dist = Vector3.Dot(p.Vel, tr.PlaneNormal) * -p.Bounce;
                                    p.Vel += tr.PlaneNormal * dist;
                                }
                            }
                        }
                    }

                    if (Vector3.Dot(p.Vel, p.Vel) < 0.03f)
                    {
                        if (p.Orientation == ParticleOrientation.Spark) { Kill(ref p, i); continue; }
                        p.Vel = Vector3.Zero;
                    }
                }

                // type post-rules (3065-3132). AlphaStatic/Static have no rule.
                if (p.TypeIndex != ParticleType.Static)
                {
                    switch (p.TypeIndex)
                    {
                        case ParticleType.EntityParticle:
                            if (p.Time2 != 0f) { Kill(ref p, i); continue; }
                            p.Time2 = 1f;
                            break;
                        case ParticleType.Blood:
                        {
                            int a = TraceSvc.PointContents(p.Org);
                            if ((a & (SuperContents.Solid | SuperContents.Lava | SuperContents.NoDrop)) != 0) { Kill(ref p, i); continue; }
                            break;
                        }
                        case ParticleType.Bubble:
                        {
                            int a = TraceSvc.PointContents(p.Org);
                            if ((a & (SuperContents.Water | SuperContents.Slime)) == 0) { Kill(ref p, i); continue; }
                            break;
                        }
                        case ParticleType.Rain:
                        {
                            int a = TraceSvc.PointContents(p.Org);
                            if ((a & (SuperContents.Solid | SuperContents.LiquidsMask)) != 0) { Kill(ref p, i); continue; }
                            break;
                        }
                        case ParticleType.Snow:
                        {
                            if (time > p.Time2)
                            {
                                // snow flutter (3092-3098) — note the DP x-into-y bug on vel[1].
                                p.Time2 = time + (Rng.NextRaw() & 3) * 0.1f;
                                p.Vel.X = p.Vel.X * 0.9f + (float)ParticleRandom.Lhrandom(Rng, -32, 32);
                                p.Vel.Y = p.Vel.X * 0.9f + (float)ParticleRandom.Lhrandom(Rng, -32, 32);
                            }
                            int a = TraceSvc.PointContents(p.Org);
                            if ((a & (SuperContents.Solid | SuperContents.LiquidsMask)) != 0) { Kill(ref p, i); continue; }
                            break;
                        }
                        default: break;
                    }
                }
            }
            else if (p.DelayedSpawn > time)
            {
                live++;
                continue;
            }

            live++;
        }

        // reduce high-water (3170-3172).
        while (_highWater > 0 && !_pool[_highWater - 1].Active) _highWater--;
        LiveCount = live;
    }

    private void Kill(ref Particle p, int i)
    {
        p.Active = false;
        p.TypeIndex = default;
        if (_freeParticle > i) _freeParticle = i;
    }

    /// <summary>Drop all live particles (map change / mode switch drain).</summary>
    public void Clear()
    {
        Array.Clear(_pool, 0, _highWater);
        _highWater = 0;
        _freeParticle = 0;
        LiveCount = 0;
        _updateTime = 0f;
    }

    // -----------------------------------------------------------------------------------------------------
    //  helpers
    // -----------------------------------------------------------------------------------------------------

    private void Grow()
    {
        int next = Math.Min(_pool.Length * 2, _maxParticles);
        if (next <= _pool.Length) return;            // already at cap
        var grown = new Particle[next];
        Array.Copy(_pool, grown, _pool.Length);
        _pool = grown;
    }

    // sv_gravity default 800 (DP cl.movevars_gravity). Read from the cvar if present, else stock 800.
    private float SvGravity()
    {
        float g = Cv("sv_gravity");
        return g != 0f ? g : 800f;
    }

    // DP trace_t exposes startsupercontents; our TraceResult does not carry it directly, so for the analytic
    // worlds we recover "started in solid" from StartSolid + the hit contents. A startsolid trace against a
    // SOLID brush sets DpHitContents to that brush's contents in the test world.
    private static int StartContents(TraceResult tr)
        => tr.StartSolid ? (tr.DpHitContents != 0 ? tr.DpHitContents : SuperContents.Solid) : 0;

    // AnglesFromVectors(angles, forward, up=NULL, flippitch=false) — mathlib.c:650. The spawn path always
    // passes up=NULL and flippitch=false. This is QMath.VecToAngles2(forward, up=null) WITHOUT the pitch flip,
    // so we port it directly here to keep the sign convention exact.
    private static Vector3 AnglesFromVectorsNoUp(Vector3 forward)
    {
        float pitch, yaw;
        if (forward.X == 0f && forward.Y == 0f)
        {
            pitch = forward.Z > 0f ? -MathF.PI * 0.5f : MathF.PI * 0.5f;
            yaw = 0f;
        }
        else
        {
            yaw = MathF.Atan2(forward.Y, forward.X);
            pitch = -MathF.Atan2(forward.Z, MathF.Sqrt(forward.X * forward.X + forward.Y * forward.Y));
        }
        pitch *= 180f / MathF.PI;
        yaw *= 180f / MathF.PI;
        // flippitch == false: NO pitch negation.
        if (pitch < 0f) pitch += 360f;
        if (yaw < 0f) yaw += 360f;
        return new Vector3(pitch, yaw, 0f);
    }

    // AngleVectors(angles, fwd, right, up) — mathlib.c. Identical to QMath.AngleVectors; inlined to keep the
    // Engine particle module free of a Common.Math dependency cycle and to read alongside the C reference.
    private static void AngleVectors(Vector3 angles, out Vector3 forward, out Vector3 right, out Vector3 up)
    {
        float ay = angles.Y * (MathF.PI / 180f); float sy = MathF.Sin(ay), cy = MathF.Cos(ay);
        float ap = angles.X * (MathF.PI / 180f); float sp = MathF.Sin(ap), cp = MathF.Cos(ap);
        float ar = angles.Z * (MathF.PI / 180f); float sr = MathF.Sin(ar), cr = MathF.Cos(ar);
        forward = new Vector3(cp * cy, cp * sy, -sp);
        right = new Vector3(-sr * sp * cy + cr * sy, -sr * sp * sy - cr * cy, -sr * cp);
        up = new Vector3(cr * sp * cy + sr * sy, cr * sp * sy - sr * cy, cr * cp);
    }
}
