/*
 * particles_ref.c — Darkplaces CPU-particle GOLDEN-TRACE generator.
 *
 * This is an INDEPENDENT C reference for the Xonotic/Darkplaces client particle
 * simulation, translated DIRECTLY and line-for-line from the engine source the
 * client actually runs:
 *
 *   Base/darkplaces/cl_particles.c
 *     * CL_NewParticle                  (lines 668-849)
 *     * CL_NewParticlesFromEffectinfo   (lines 1569-1788)  — the non-trail/non-beam path
 *     * R_DrawParticles update loop      (lines 2907-3132)
 *   Base/darkplaces/mathlib.h
 *     * lhrandom                         (line 48)
 *     * VectorRandom                     (line 119)
 *     * AnglesFromVectors / AngleVectors (mathlib.c)
 *
 * It is deliberately NOT derived from the C# port (src/VortexArena.Engine/Particles):
 * the whole point of a golden corpus is an *independent* implementation to A/B the
 * port against. The particle math is float (vec3_t is float in DP); lhrandom is
 * computed in double exactly as the macro does.
 *
 * RNG: glibc rand() with a fixed srand() seed. EVERY rand() return value is recorded
 * (in call order) into the scenario's "rng":[...] array, so the C# parity harness can
 * replay the identical integer stream through RecordedParticleRng and any divergence
 * is pure math, never RNG drift. We wrap rand() in rec_rand() to capture it.
 *
 * Collision: a MINIMAL analytic world of axis-aligned half-space planes (one or two
 * brushes: a solid floor/wall + an optional water box). pointcontents() ORs the
 * contents of every brush containing the point; traceline() is a swept POINT trace
 * (mins=maxs=0) against the SOLID brushes only, reproduced VERBATIM in C# (the test's
 * ParticleAnalyticWorld) so collision is bit-identical on both sides.
 *
 * Output: one JSON file per scenario, holding the emitter params, the spawn inputs,
 * the world planes, the recorded rng sequence, and a per-step per-particle
 * [org, vel, size, alpha, active] dump.
 *
 * Build:  gcc -O2 -std=c11 -o particles_ref particles_ref.c -lm
 * Run:    ./particles_ref <output-dir>
 */

#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <math.h>
#include <stdbool.h>

/* ===================================================================== *
 *  RNG — glibc rand(), every draw recorded in call order
 * ===================================================================== */
#define MAX_RNG 1000000
static int   g_rng[MAX_RNG];
static int   g_rng_count;

static int rec_rand(void)
{
    int r = rand();                 /* glibc rand(): [0, RAND_MAX] == [0, 2147483647] */
    if (g_rng_count < MAX_RNG)
        g_rng[g_rng_count] = r;
    g_rng_count++;
    return r;
}

/* lhrandom(MIN,MAX) — mathlib.h:48. Computed in double, never returns exactly MIN/MAX. */
#define lhrandom(MIN,MAX) (((double)(rec_rand() + 0.5) / ((double)RAND_MAX + 1)) * ((MAX)-(MIN)) + (MIN))

/* ===================================================================== *
 *  vector math (Quake float3)
 * ===================================================================== */
typedef struct { float x, y, z; } qvec;
static inline qvec v3(float x, float y, float z){ qvec r={x,y,z}; return r; }
static inline qvec vadd(qvec a, qvec b){ return v3(a.x+b.x,a.y+b.y,a.z+b.z); }
static inline qvec vsub(qvec a, qvec b){ return v3(a.x-b.x,a.y-b.y,a.z-b.z); }
static inline qvec vscale(qvec a, float s){ return v3(a.x*s,a.y*s,a.z*s); }
static inline float vdot(qvec a, qvec b){ return a.x*b.x+a.y*b.y+a.z*b.z; }
static inline float vlen(qvec a){ return (float)sqrt((double)vdot(a,a)); }

#define M_PI_F 3.14159265358979323846

/* VectorRandom(v) — mathlib.h:119: rejection-sample the unit ball, float components. */
static qvec vector_random(void)
{
    qvec v;
    do {
        v.x = (float)lhrandom(-1, 1);
        v.y = (float)lhrandom(-1, 1);
        v.z = (float)lhrandom(-1, 1);
    } while (vdot(v,v) > 1.0f);
    return v;
}

/* AngleVectors — mathlib.c. pitch=X (down-positive), yaw=Y, roll=Z (degrees). */
static void angle_vectors(qvec ang, qvec *fwd, qvec *right, qvec *up)
{
    float ay = ang.y*(float)(M_PI_F/180.0), sy=sinf(ay), cy=cosf(ay);
    float ap = ang.x*(float)(M_PI_F/180.0), sp=sinf(ap), cp=cosf(ap);
    float ar = ang.z*(float)(M_PI_F/180.0), sr=sinf(ar), cr=cosf(ar);
    if (fwd)   *fwd   = v3(cp*cy, cp*sy, -sp);
    if (right) *right = v3(-sr*sp*cy + cr*sy, -sr*sp*sy - cr*cy, -sr*cp);
    if (up)    *up    = v3( cr*sp*cy + sr*sy,  cr*sp*sy - sr*cy,  cr*cp);
}

/* AnglesFromVectors(angles, forward, up=NULL, flippitch=false) — mathlib.c:650.
   The spawn path always passes up=NULL and flippitch=false. */
static qvec angles_from_vectors_noup(qvec forward)
{
    qvec a;
    if (forward.x == 0 && forward.y == 0) {
        if (forward.z > 0) { a.x = (float)(-M_PI_F*0.5); a.y = 0; }
        else               { a.x = (float)( M_PI_F*0.5); a.y = 0; }
        a.z = 0;
    } else {
        a.y = atan2f(forward.y, forward.x);
        a.x = -atan2f(forward.z, sqrtf(forward.x*forward.x + forward.y*forward.y));
        a.z = 0;
    }
    a = vscale(a, (float)(180.0/M_PI_F));
    /* flippitch == false: no pitch negation */
    if (a.x < 0) a.x += 360;
    if (a.y < 0) a.y += 360;
    if (a.z < 0) a.z += 360;
    return a;
}

/* ===================================================================== *
 *  SUPERCONTENTS / surface flags (Base/darkplaces/bspfile.h + Q3 flags)
 * ===================================================================== */
#define SC_SOLID       0x00000001
#define SC_WATER       0x00000010
#define SC_SLIME       0x00000020
#define SC_LAVA        0x00000040
#define SC_NODROP      ((int)0x80000000)
#define SC_LIQUIDSMASK (SC_WATER|SC_SLIME|SC_LAVA)

#define Q3SURF_NOIMPACT 0x0010
#define Q3SURF_NOMARKS  0x0020

/* ===================================================================== *
 *  Analytic collision world (mirrored verbatim in C#)
 * ===================================================================== */
typedef struct { qvec normal; float dist; } cplane;     /* inside: dot(n,p) <= dist */
typedef struct { cplane planes[8]; int nplanes; int contents; int surfaceflags; } cbrush;
typedef struct { cbrush brushes[8]; int nbrushes; } cworld;

typedef struct {
    float fraction; qvec endpos; qvec plane_normal;
    int startsolid; int allsolid; int hitcontents; int startcontents; int hitsurfaceflags;
} ctrace;

static cbrush box_brush(qvec mins, qvec maxs, int contents, int surfaceflags){
    cbrush b; b.nplanes=0; b.contents=contents; b.surfaceflags=surfaceflags;
    b.planes[b.nplanes++] = (cplane){ v3( 1,0,0),  maxs.x };
    b.planes[b.nplanes++] = (cplane){ v3(-1,0,0), -mins.x };
    b.planes[b.nplanes++] = (cplane){ v3(0, 1,0),  maxs.y };
    b.planes[b.nplanes++] = (cplane){ v3(0,-1,0), -mins.y };
    b.planes[b.nplanes++] = (cplane){ v3(0,0, 1),  maxs.z };
    b.planes[b.nplanes++] = (cplane){ v3(0,0,-1), -mins.z };
    return b;
}

#define TRACE_DIST_EPSILON (1.0f/32.0f)

/* clip a swept POINT (mins=maxs=0) against one convex brush. Faithful to
   Collision_TraceLineBrushFloat (no hull expansion since the point has zero size). */
static void clip_point_to_brush(ctrace *tr, qvec start, qvec end, const cbrush *b){
    if (b->nplanes==0) return;
    float enterfrac=-1.0f, leavefrac=1.0f;
    qvec clipnormal=v3(0,0,0);
    bool startout=false, endout=false;
    for (int i=0;i<b->nplanes;i++){
        qvec n=b->planes[i].normal;
        float d=b->planes[i].dist;
        float d1=vdot(n,start)-d;
        float d2=vdot(n,end)-d;
        if (d1>0) startout=true;
        if (d2>0) endout=true;
        if (d1>0 && d2>=0) return;          /* wholly outside this plane -> no hit */
        if (d1<=0 && d2<=0) continue;        /* wholly inside this halfspace */
        if (d1>d2){                          /* entering */
            float f=(d1-TRACE_DIST_EPSILON)/(d1-d2);
            if (f>enterfrac){ enterfrac=f; clipnormal=n; }
        } else {                             /* leaving */
            float f=(d1+TRACE_DIST_EPSILON)/(d1-d2);
            if (f<leavefrac) leavefrac=f;
        }
    }
    if (!startout){                          /* began inside the brush */
        tr->startsolid=1; tr->startcontents|=b->contents;
        if (!endout) tr->allsolid=1;
        return;
    }
    if (enterfrac<leavefrac && enterfrac>-1.0f){
        if (enterfrac<0.0f) enterfrac=0.0f;
        if (enterfrac<tr->fraction){
            tr->fraction=enterfrac;
            tr->plane_normal=clipnormal;
            tr->hitcontents=b->contents;
            tr->hitsurfaceflags=b->surfaceflags;
        }
    }
}

/* traceline(start->end) vs the world's SOLID(+optionally LIQUIDS) brushes. */
static ctrace world_trace(const cworld *w, qvec start, qvec end, int hitmask){
    ctrace tr; tr.fraction=1.0f; tr.endpos=end; tr.plane_normal=v3(0,0,0);
    tr.startsolid=0; tr.allsolid=0; tr.hitcontents=0; tr.startcontents=0; tr.hitsurfaceflags=0;
    for (int i=0;i<w->nbrushes;i++){
        if (!(w->brushes[i].contents & hitmask)) continue;
        clip_point_to_brush(&tr, start, end, &w->brushes[i]);
        if (tr.allsolid) tr.fraction=0.0f;
    }
    if (tr.startsolid) tr.fraction = tr.allsolid ? 0.0f : tr.fraction;
    tr.endpos = vadd(start, vscale(vsub(end,start), tr.fraction));
    return tr;
}

static int world_pointcontents(const cworld *w, qvec p){
    int c=0;
    for (int i=0;i<w->nbrushes;i++){
        const cbrush *b=&w->brushes[i];
        bool inside=true;
        for (int j=0;j<b->nplanes;j++)
            if (vdot(b->planes[j].normal,p)-b->planes[j].dist > 0){ inside=false; break; }
        if (inside) c |= b->contents;
    }
    return c;
}

/* ===================================================================== *
 *  particle_t (cl_particles.h:80-120, the fields the sim touches)
 * ===================================================================== */
/* DP ptype_t (cl_particles.h:59-78): pt_dead == 0 is the FREE-slot sentinel, so the first real
   kind pt_alphastatic == 1. Liveness in DP is typeindex != 0. The C# ParticleType enum has NO
   Dead member (AlphaStatic == 0), so when we emit the block "type" field for the C# harness we
   write the DP value MINUS 1 (see emit_json). Here we keep DP's numbering. */
enum {
    pt_dead=0, pt_alphastatic, pt_static, pt_spark, pt_beam, pt_rain, pt_raindecal,
    pt_snow, pt_bubble, pt_blood, pt_smoke, pt_decal, pt_entityparticle, pt_explode, pt_explode2
};
enum { PBLEND_ALPHA=0, PBLEND_ADD, PBLEND_INVMOD };
enum { PARTICLE_BILLBOARD=0, PARTICLE_SPARK, PARTICLE_ORIENTED, PARTICLE_BEAM };

typedef struct {
    int active;                 /* typeindex != 0 in DP; we track an explicit flag */
    qvec org, vel;
    float size, alpha, stretch;
    float stainsize, stainalpha;
    float sizeincrease, alphafade, time2, bounce, gravity, airfriction, liquidfriction;
    float delayedspawn, die;
    float angle, spin;
    unsigned char color[3], staincolor[3];
    int typeindex, blendmode, orientation;
    int texnum;
    int staintexnum;
} particle_t;

#define MAX_PART 4096
static particle_t parts[MAX_PART];
static int g_num_particles;     /* high-water (cl.num_particles) */
static int g_free_particle;     /* low-water free slot (cl.free_particle) */

/* the simulated client globals */
static const cworld *g_world;
static float g_time;            /* cl.time */
static float g_particles_updatetime;
static float g_movevars_gravity = 800.0f;
static int   g_collisions = 1;  /* cl_particles_collisions */

/* ===================================================================== *
 *  effectinfo block (particleeffectinfo_t subset) — defaults at baseline
 * ===================================================================== */
typedef struct {
    float countabsolute, countmultiplier, trailspacing;
    int particletype, blendmode, orientation;
    unsigned int color[2];
    int tex[2];
    float size[3];        /* min,max,sizeincrease */
    float alpha[3];       /* min,max,alphafade */
    float time[2];        /* min,max */
    float gravity, bounce, airfriction, liquidfriction, stretchfactor;
    qvec originoffset, relativeoriginoffset, velocityoffset, relativevelocityoffset;
    qvec originjitter, velocityjitter;
    float velocitymultiplier;
    unsigned int staincolor[2];
    int staintex[2];
    float stainalpha[2], stainsize[2];
    float rotate[4];      /* base min/max, spin min/max */
    int underwater, notunderwater;
    double particleaccumulator;
} einfo;

static einfo baseline_einfo(void){
    einfo e; memset(&e,0,sizeof(e));
    e.countmultiplier=0; e.particletype=pt_alphastatic; e.blendmode=PBLEND_ALPHA; e.orientation=PARTICLE_BILLBOARD;
    e.color[0]=0xFFFFFF; e.color[1]=0xFFFFFF;
    e.tex[0]=63; e.tex[1]=63;
    e.size[0]=1; e.size[1]=1; e.size[2]=0;
    e.alpha[0]=0; e.alpha[1]=256; e.alpha[2]=256;
    e.time[0]=16777216.0f; e.time[1]=16777216.0f;
    e.stretchfactor=1;
    e.staincolor[0]=(unsigned int)-1; e.staincolor[1]=(unsigned int)-1;
    e.staintex[0]=-1; e.staintex[1]=-1;
    e.stainalpha[0]=1; e.stainalpha[1]=1; e.stainsize[0]=2; e.stainsize[1]=2;
    e.rotate[1]=360;
    return e;
}

/* cvar gates (defaults all 1) */
static int cv_particles=1;
static float cv_quality=1.0f;
static int cv_blood=1, cv_sparks=1, cv_smoke=1, cv_bubbles=1, cv_rain=1, cv_snow=1;

/* ===================================================================== *
 *  CL_NewParticle (cl_particles.c:668-849), non-rain branch faithful;
 *  rain branch ported best-effort (turn into spark + traced die).
 * ===================================================================== */
static int new_particle(
    int ptypeindex, int pcolor1, int pcolor2, int ptex,
    float psize, float psizeincrease, float palpha, float palphafade,
    float pgravity, float pbounce,
    float px, float py, float pz, float pvx, float pvy, float pvz,
    float pairfriction, float pliquidfriction,
    float lifetime, float stretch, int blendmode, int orientation,
    int staincolor1, int staincolor2, int staintex,
    float stainalpha, float stainsize, float angle, float spin,
    const float *tint /* may be NULL, 4 floats */)
{
    int l1,l2,r,g,b;
    if (!cv_particles) return -1;
    for (; g_free_particle < MAX_PART && parts[g_free_particle].typeindex; g_free_particle++);
    if (g_free_particle >= MAX_PART) return -1;
    if (!lifetime) lifetime = palpha / fminf(1.0f, palphafade);
    int idx = g_free_particle++;
    particle_t *p = &parts[idx];
    if (g_num_particles < g_free_particle) g_num_particles = g_free_particle;
    memset(p, 0, sizeof(*p));
    p->active = 1;
    p->typeindex = ptypeindex;
    p->blendmode = blendmode;
    p->orientation = orientation;      /* beams collapse to V/HBEAM in DP; our scenarios don't use beams */
    l2 = (int)lhrandom(0.5, 256.5);
    l1 = 256 - l2;
    p->color[0] = ((((pcolor1>>16)&0xFF)*l1 + ((pcolor2>>16)&0xFF)*l2)>>8)&0xFF;
    p->color[1] = ((((pcolor1>> 8)&0xFF)*l1 + ((pcolor2>> 8)&0xFF)*l2)>>8)&0xFF;
    p->color[2] = ((((pcolor1>> 0)&0xFF)*l1 + ((pcolor2>> 0)&0xFF)*l2)>>8)&0xFF;
    /* (no sRGB3D conversion: vid.sRGB3D is false in the parity world) */
    p->alpha = palpha;
    p->alphafade = palphafade;
    p->staintexnum = staintex;
    if (staincolor1 >= 0 && staincolor2 >= 0){
        l2 = (int)lhrandom(0.5, 256.5);
        l1 = 256 - l2;
        if (blendmode == PBLEND_INVMOD){
            r = ((((staincolor1>>16)&0xFF)*l1 + ((staincolor2>>16)&0xFF)*l2) * (255 - p->color[0])) / 0x8000;
            g = ((((staincolor1>> 8)&0xFF)*l1 + ((staincolor2>> 8)&0xFF)*l2) * (255 - p->color[1])) / 0x8000;
            b = ((((staincolor1>> 0)&0xFF)*l1 + ((staincolor2>> 0)&0xFF)*l2) * (255 - p->color[2])) / 0x8000;
        } else {
            r = ((((staincolor1>>16)&0xFF)*l1 + ((staincolor2>>16)&0xFF)*l2) * p->color[0]) / 0x8000;
            g = ((((staincolor1>> 8)&0xFF)*l1 + ((staincolor2>> 8)&0xFF)*l2) * p->color[1]) / 0x8000;
            b = ((((staincolor1>> 0)&0xFF)*l1 + ((staincolor2>> 0)&0xFF)*l2) * p->color[2]) / 0x8000;
        }
        if (r>0xFF) r=0xFF;
        if (g>0xFF) g=0xFF;
        if (b>0xFF) b=0xFF;
    } else {
        r = p->color[0]; g = p->color[1]; b = p->color[2];
    }
    p->staincolor[0]=r; p->staincolor[1]=g; p->staincolor[2]=b;
    p->stainalpha = palpha * stainalpha;
    p->stainsize  = psize * stainsize;
    if (tint){
        if (blendmode != PBLEND_INVMOD){
            p->color[0] = (unsigned char)(p->color[0]*tint[0]);
            p->color[1] = (unsigned char)(p->color[1]*tint[1]);
            p->color[2] = (unsigned char)(p->color[2]*tint[2]);
        }
        p->alpha *= tint[3];
        p->alphafade *= tint[3];
        p->stainalpha *= tint[3];
    }
    p->texnum = ptex;
    p->size = psize;
    p->sizeincrease = psizeincrease;
    p->gravity = pgravity;
    p->bounce = pbounce;
    p->stretch = stretch;
    {
        qvec v = vector_random();
        p->org.x = px + 0 * v.x; /* originjitter folded into px by the caller in the effectinfo path */
        p->org.y = py + 0 * v.y;
        p->org.z = pz + 0 * v.z;
        p->vel.x = pvx + 0 * v.x;
        p->vel.y = pvy + 0 * v.y;
        p->vel.z = pvz + 0 * v.z;
        /* NOTE: In CL_NewParticle the originjitter/velocityjitter args ARE applied here with a
           FRESH VectorRandom. But CL_NewParticlesFromEffectinfo passes originjitter=0,
           velocityjitter=0 to CL_NewParticle (it applied its own shared rvec to px/pvx already),
           so the only effect of this VectorRandom is to consume one ball sample. We reproduce that
           consumption exactly: the effectinfo path passes 0 jitter, so org/vel == px/pvx. */
        (void)v;
    }
    p->airfriction = pairfriction;
    p->liquidfriction = pliquidfriction;
    p->die = g_time + lifetime;
    p->delayedspawn = g_time;
    p->angle = angle;
    p->spin = spin;

    if (p->typeindex == pt_rain){
        /* turn raindrop into a spark and create a delayedspawn splash (804-832), best-effort.
           We keep the spark + die-by-trace; the splash sub-particles consume rand() exactly. */
        qvec endvec;
        p->typeindex = pt_spark;
        p->bounce = 0;
        endvec = vadd(p->org, vscale(p->vel, lifetime));
        ctrace tr = world_trace(g_world, p->org, endvec, SC_SOLID | SC_LIQUIDSMASK);
        p->die = g_time + lifetime * tr.fraction;
        /* raindecal sub-particle (no rand of its own beyond CL_NewParticle's color lerp + VectorRandom). */
        qvec dorg = vadd(tr.endpos, tr.plane_normal);
        int rd = new_particle(pt_raindecal, pcolor1, pcolor2, 0, p->size, p->size*20, p->alpha, p->alpha/0.4f,
                              0,0, dorg.x,dorg.y,dorg.z, tr.plane_normal.x,tr.plane_normal.y,tr.plane_normal.z,
                              0,0, 1, stretch, PBLEND_ADD, PARTICLE_ORIENTED, -1,-1,-1, 1,1, 0,0, NULL);
        if (rd >= 0){
            parts[rd].delayedspawn = p->die;
            parts[rd].die += p->die - g_time;
            for (int i = rec_rand() & 7; i < 10; i++){
                qvec sorg = vadd(tr.endpos, tr.plane_normal);
                qvec svel = v3(tr.plane_normal.x*16, tr.plane_normal.y*16, tr.plane_normal.z*16 + g_movevars_gravity*0.04f);
                int sp2 = new_particle(pt_spark, pcolor1, pcolor2, 0, 0.25f, 0, p->alpha*2, p->alpha*4,
                                       1, 0.1f, sorg.x,sorg.y,sorg.z, svel.x,svel.y,svel.z,
                                       0,0, 32, stretch, PBLEND_ADD, PARTICLE_SPARK, -1,-1,-1, 1,1, 0,0, NULL);
                if (sp2 >= 0){
                    parts[sp2].delayedspawn = p->die;
                    parts[sp2].die += p->die - g_time;
                }
            }
        }
    }
    return idx;
}

/* ===================================================================== *
 *  CL_NewParticlesFromEffectinfo (1569-1788) — non-trail/non-beam path
 * ===================================================================== */
static void spawn_effect(einfo *blocks, int nblocks, float pcount,
                         qvec originmins, qvec originmaxs, qvec velocitymins, qvec velocitymaxs,
                         const float *tintmins /*4 or NULL*/, const float *tintmaxs,
                         float fade, int wanttrail)
{
    qvec center = vscale(vadd(originmins, vscale(vsub(originmaxs,originmins),0.5f)),1.0f); /* lerp 0.5 */
    center = v3((originmins.x+originmaxs.x)*0.5f,(originmins.y+originmaxs.y)*0.5f,(originmins.z+originmaxs.z)*0.5f);
    int supercontents = world_pointcontents(g_world, center);
    int underwater = (supercontents & (SC_WATER|SC_SLIME)) != 0;
    qvec traildir = vsub(originmaxs, originmins);
    float traillen = vlen(traildir);
    /* VectorNormalize(traildir): zero -> stays zero in DP (ilength stays 0) */
    {
        float il = vdot(traildir,traildir);
        if (il) { il = 1.0f/sqrtf(il); traildir = vscale(traildir, il); }
    }
    float avgtint[4];
    if (tintmins){ for(int k=0;k<4;k++) avgtint[k]=tintmins[k]+0.5f*(tintmaxs[k]-tintmins[k]); }
    else { avgtint[0]=avgtint[1]=avgtint[2]=avgtint[3]=1; }

    for (int bi=0; bi<nblocks; bi++){
        einfo *info = &blocks[bi];
        int definedastrail = info->trailspacing > 0;
        int drawastrail = wanttrail;   /* cl_particles_forcetraileffects default 0 */

        if (info->underwater && !underwater) continue;
        if (info->notunderwater && underwater) continue;

        /* (no dlight in the parity world) */

        int tex = info->tex[0];
        if (info->tex[1] > info->tex[0]){
            tex = (int)lhrandom(info->tex[0], info->tex[1]);
            if (tex > info->tex[1]-1) tex = info->tex[1]-1;
        }
        int staintex;
        if (info->staintex[0] < 0) staintex = info->staintex[0];
        else {
            staintex = (int)lhrandom(info->staintex[0], info->staintex[1]);
            if (staintex > info->staintex[1]-1) staintex = info->staintex[1]-1;
        }

        /* decal / HBEAM paths omitted — our scenarios never use them. The "else" path: */
        {
            if (!cv_particles) continue;
            switch (info->particletype){
            case pt_smoke:  if (!cv_smoke)   continue; break;
            case pt_spark:  if (!cv_sparks)  continue; break;
            case pt_bubble: if (!cv_bubbles) continue; break;
            case pt_blood:  if (!cv_blood)   continue; break;
            case pt_rain:   if (!cv_rain)    continue; break;
            case pt_snow:   if (!cv_snow)    continue; break;
            default: break;
            }

            float cnt = info->countabsolute;
            cnt += (pcount * info->countmultiplier) * cv_quality;
            if (drawastrail && definedastrail)
                cnt += (traillen / info->trailspacing) * cv_quality;
            cnt *= fade;
            if (cnt == 0) continue;
            info->particleaccumulator += cnt;

            int immediatebloodstain = 0; /* not modeled — no decals in the analytic world */

            qvec trailpos, velocity, angles, forward, right, up;
            float trailstep;
            if (drawastrail){
                trailpos = originmins;
                trailstep = traillen / cnt;
            } else {
                trailpos = center;
                trailstep = 0;
            }

            if (trailstep == 0){
                velocity = v3((velocitymins.x+velocitymaxs.x)*0.5f,
                              (velocitymins.y+velocitymaxs.y)*0.5f,
                              (velocitymins.z+velocitymaxs.z)*0.5f);
                angles = angles_from_vectors_noup(velocity);
            } else {
                angles = angles_from_vectors_noup(traildir);
            }
            angle_vectors(angles, &forward, &right, &up);

            /* VectorMAMAMAM(1, trailpos, rel[0], fwd, rel[1], right, rel[2], up, trailpos) */
            trailpos = vadd(trailpos,
                       vadd(vscale(forward, info->relativeoriginoffset.x),
                       vadd(vscale(right,   info->relativeoriginoffset.y),
                            vscale(up,      info->relativeoriginoffset.z))));
            /* VectorMAMAM(relvel[0],fwd, relvel[1],right, relvel[2],up, velocity) */
            velocity = vadd(vscale(forward, info->relativevelocityoffset.x),
                       vadd(vscale(right,   info->relativevelocityoffset.y),
                            vscale(up,       info->relativevelocityoffset.z)));

            if (info->particleaccumulator < 0) info->particleaccumulator = 0;
            if (info->particleaccumulator > 16384) info->particleaccumulator = 16384;

            for (; info->particleaccumulator >= 1; info->particleaccumulator -= 1){
                if (info->tex[1] > info->tex[0]){
                    tex = (int)lhrandom(info->tex[0], info->tex[1]);
                    if (tex > info->tex[1]-1) tex = info->tex[1]-1;
                }
                if (!(drawastrail || definedastrail)){
                    trailpos.x = (float)lhrandom(originmins.x, originmaxs.x);
                    trailpos.y = (float)lhrandom(originmins.y, originmaxs.y);
                    trailpos.z = (float)lhrandom(originmins.z, originmaxs.z);
                }
                float tint[4]; const float *tintptr=NULL;
                if (tintmins){
                    float tl = (float)lhrandom(0,1);
                    for(int k=0;k<4;k++) tint[k]=tintmins[k]+tl*(tintmaxs[k]-tintmins[k]);
                    tintptr=tint;
                }
                qvec rvec = vector_random();
                float ox = trailpos.x + info->originoffset.x + info->originjitter.x * rvec.x;
                float oy = trailpos.y + info->originoffset.y + info->originjitter.y * rvec.y;
                float oz = trailpos.z + info->originoffset.z + info->originjitter.z * rvec.z;
                float vx = (float)lhrandom(velocitymins.x,velocitymaxs.x)*info->velocitymultiplier + info->velocityoffset.x + info->velocityjitter.x*rvec.x + velocity.x;
                float vy = (float)lhrandom(velocitymins.y,velocitymaxs.y)*info->velocitymultiplier + info->velocityoffset.y + info->velocityjitter.y*rvec.y + velocity.y;
                float vz = (float)lhrandom(velocitymins.z,velocitymaxs.z)*info->velocitymultiplier + info->velocityoffset.z + info->velocityjitter.z*rvec.z + velocity.z;
                float psize = (float)lhrandom(info->size[0],info->size[1]);
                float palpha = (float)lhrandom(info->alpha[0],info->alpha[1]);
                float lifetime = (float)lhrandom(info->time[0],info->time[1]);
                float pstainalpha = (float)lhrandom(info->stainalpha[0],info->stainalpha[1]);
                float pstainsize = (float)lhrandom(info->stainsize[0],info->stainsize[1]);
                float pangle = (float)lhrandom(info->rotate[0],info->rotate[1]);
                float pspin = (float)lhrandom(info->rotate[2],info->rotate[3]);
                new_particle(info->particletype, info->color[0], info->color[1], tex,
                             psize, info->size[2], palpha, info->alpha[2],
                             info->gravity, info->bounce, ox,oy,oz, vx,vy,vz,
                             info->airfriction, info->liquidfriction,
                             lifetime, info->stretchfactor, info->blendmode, info->orientation,
                             info->staincolor[0], info->staincolor[1], staintex,
                             pstainalpha, pstainsize, pangle, pspin, tintptr);
                (void)immediatebloodstain;
                if (trailstep) trailpos = vadd(trailpos, vscale(traildir, trailstep));
            }
        }
    }
}

/* ===================================================================== *
 *  R_DrawParticles update (2907-3132)
 * ===================================================================== */
static void update_particles(float newtime){
    g_time = newtime;
    float frametime = newtime - g_particles_updatetime;
    if (frametime < 0) frametime = 0;
    if (frametime > 1) frametime = 1;
    float lo = newtime - 1, hi = newtime + 1;
    float ut = g_particles_updatetime + frametime;
    if (ut < lo) ut = lo;
    if (ut > hi) ut = hi;
    g_particles_updatetime = ut;

    if (!g_num_particles) return;

    float gravity = frametime * g_movevars_gravity;
    int update = frametime > 0;

    for (int i=0;i<g_num_particles;i++){
        particle_t *p = &parts[i];
        if (!p->typeindex){
            if (g_free_particle > i) g_free_particle = i;
            continue;
        }
        if (update){
            if (p->delayedspawn > g_time) continue;
            p->size += p->sizeincrease * frametime;
            p->alpha -= p->alphafade * frametime;
            if (p->alpha <= 0 || p->die <= g_time) goto killparticle;

            if (p->orientation != PARTICLE_BEAM && frametime > 0){
                float f;
                if (p->liquidfriction && g_collisions && (world_pointcontents(g_world,p->org)&SC_LIQUIDSMASK)){
                    if (p->typeindex == pt_blood) p->size += frametime*8;
                    else p->vel.z -= p->gravity * gravity;
                    f = 1.0f - fminf(p->liquidfriction*frametime, 1.0f);
                    p->vel = vscale(p->vel, f);
                } else {
                    p->vel.z -= p->gravity * gravity;
                    if (p->airfriction){
                        f = 1.0f - fminf(p->airfriction*frametime, 1.0f);
                        p->vel = vscale(p->vel, f);
                    }
                }

                qvec oldorg = p->org;
                p->org = vadd(p->org, vscale(p->vel, frametime));
                if (p->bounce && g_collisions && vlen(p->vel)){
                    int hitmask = SC_SOLID | ((p->typeindex==pt_rain||p->typeindex==pt_snow)?SC_LIQUIDSMASK:0);
                    ctrace tr = world_trace(g_world, oldorg, p->org, hitmask);
                    if ((tr.hitsurfaceflags & Q3SURF_NOIMPACT)
                        || ((tr.startcontents | tr.hitcontents) & SC_NODROP)
                        || (tr.startcontents & SC_SOLID))
                        goto killparticle;
                    p->org = tr.endpos;
                    if (tr.fraction < 1){
                        p->org = tr.endpos;
                        /* stain/decal emission omitted in the analytic world */
                        if (p->typeindex == pt_blood){
                            if (tr.hitsurfaceflags & Q3SURF_NOMARKS) goto killparticle;
                            goto killparticle;
                        } else if (p->bounce < 0){
                            goto killparticle;
                        } else {
                            float dist = vdot(p->vel, tr.plane_normal) * -p->bounce;
                            p->vel = vadd(p->vel, vscale(tr.plane_normal, dist));
                        }
                    }
                }
                if (vdot(p->vel,p->vel) < 0.03f){
                    if (p->orientation == PARTICLE_SPARK) goto killparticle;
                    p->vel = v3(0,0,0);
                }
            }

            if (p->typeindex != pt_static){
                int a;
                switch (p->typeindex){
                case pt_entityparticle:
                    if (p->time2) goto killparticle; else p->time2=1;
                    break;
                case pt_blood:
                    a = world_pointcontents(g_world,p->org);
                    if (a & (SC_SOLID|SC_LAVA|SC_NODROP)) goto killparticle;
                    break;
                case pt_bubble:
                    a = world_pointcontents(g_world,p->org);
                    if (!(a & (SC_WATER|SC_SLIME))) goto killparticle;
                    break;
                case pt_rain:
                    a = world_pointcontents(g_world,p->org);
                    if (a & (SC_SOLID|SC_LIQUIDSMASK)) goto killparticle;
                    break;
                case pt_snow:
                    if (g_time > p->time2){
                        p->time2 = g_time + (rec_rand()&3)*0.1f;
                        p->vel.x = p->vel.x*0.9f + (float)lhrandom(-32,32);
                        p->vel.y = p->vel.x*0.9f + (float)lhrandom(-32,32);  /* DP x-into-y bug, line 3097 */
                    }
                    a = world_pointcontents(g_world,p->org);
                    if (a & (SC_SOLID|SC_LIQUIDSMASK)) goto killparticle;
                    break;
                default: break;
                }
            }
        } else if (p->delayedspawn > g_time) {
            continue;
        }
        continue;
killparticle:
        p->typeindex = 0;
        p->active = 0;
        if (g_free_particle > i) g_free_particle = i;
    }

    while (g_num_particles > 0 && parts[g_num_particles-1].typeindex == 0)
        g_num_particles--;
}

/* ===================================================================== *
 *  scenario harness + JSON emitter
 * ===================================================================== */
#define MAX_STEPS 64
typedef struct {
    const char *name, *desc;
    cworld world;
    einfo blocks[4]; int nblocks;
    float pcount, fade;
    qvec originmins, originmaxs, velocitymins, velocitymaxs;
    int wanttrail;
    int nsteps;
    float dts[MAX_STEPS];
    int collisions;
} scenario;

static void reset_sim(const cworld *w){
    memset(parts,0,sizeof(parts));
    g_num_particles=0; g_free_particle=0;
    g_world=w; g_time=0; g_particles_updatetime=0;
}

static void emit_json(FILE *f, scenario *s){
    g_rng_count=0;
    g_collisions = s->collisions;
    reset_sim(&s->world);

    /* spawn at t=0 */
    spawn_effect(s->blocks, s->nblocks, s->pcount,
                 s->originmins, s->originmaxs, s->velocitymins, s->velocitymaxs,
                 NULL, NULL, s->fade, s->wanttrail);

    int spawn_rng = g_rng_count;   /* count consumed by spawn (vs by update) */

    fprintf(f,"{\n");
    fprintf(f,"  \"name\": \"%s\",\n", s->name);
    fprintf(f,"  \"description\": \"%s\",\n", s->desc);
    fprintf(f,"  \"collisions\": %d,\n", s->collisions);
    fprintf(f,"  \"world\": [\n");
    for (int i=0;i<s->world.nbrushes;i++){
        cbrush *b=&s->world.brushes[i];
        fprintf(f,"    { \"contents\": %d, \"surfaceflags\": %d, \"planes\": [", b->contents, b->surfaceflags);
        for (int j=0;j<b->nplanes;j++)
            fprintf(f,"%s[%.9g,%.9g,%.9g,%.9g]", j?",":"",
                    b->planes[j].normal.x,b->planes[j].normal.y,b->planes[j].normal.z,b->planes[j].dist);
        fprintf(f,"] }%s\n", i+1<s->world.nbrushes?",":"");
    }
    fprintf(f,"  ],\n");

    /* emitter blocks */
    fprintf(f,"  \"blocks\": [\n");
    for (int bi=0; bi<s->nblocks; bi++){
        einfo *e=&s->blocks[bi];
        fprintf(f,"    {");
        fprintf(f,"\"countabsolute\":%.9g,\"countmultiplier\":%.9g,\"trailspacing\":%.9g,",
                e->countabsolute,e->countmultiplier,e->trailspacing);
        /* DP type - 1 == C# ParticleType (no Dead member); blend/orientation share numbering. */
        fprintf(f,"\"type\":%d,\"blend\":%d,\"orientation\":%d,",e->particletype-1,e->blendmode,e->orientation);
        fprintf(f,"\"color0\":%u,\"color1\":%u,",e->color[0],e->color[1]);
        fprintf(f,"\"tex0\":%d,\"tex1\":%d,",e->tex[0],e->tex[1]);
        fprintf(f,"\"size\":[%.9g,%.9g,%.9g],",e->size[0],e->size[1],e->size[2]);
        fprintf(f,"\"alpha\":[%.9g,%.9g,%.9g],",e->alpha[0],e->alpha[1],e->alpha[2]);
        fprintf(f,"\"time\":[%.9g,%.9g],",e->time[0],e->time[1]);
        fprintf(f,"\"gravity\":%.9g,\"bounce\":%.9g,\"airfriction\":%.9g,\"liquidfriction\":%.9g,\"stretchfactor\":%.9g,\"velocitymultiplier\":%.9g,",
                e->gravity,e->bounce,e->airfriction,e->liquidfriction,e->stretchfactor,e->velocitymultiplier);
        fprintf(f,"\"originoffset\":[%.9g,%.9g,%.9g],",e->originoffset.x,e->originoffset.y,e->originoffset.z);
        fprintf(f,"\"relativeoriginoffset\":[%.9g,%.9g,%.9g],",e->relativeoriginoffset.x,e->relativeoriginoffset.y,e->relativeoriginoffset.z);
        fprintf(f,"\"velocityoffset\":[%.9g,%.9g,%.9g],",e->velocityoffset.x,e->velocityoffset.y,e->velocityoffset.z);
        fprintf(f,"\"relativevelocityoffset\":[%.9g,%.9g,%.9g],",e->relativevelocityoffset.x,e->relativevelocityoffset.y,e->relativevelocityoffset.z);
        fprintf(f,"\"originjitter\":[%.9g,%.9g,%.9g],",e->originjitter.x,e->originjitter.y,e->originjitter.z);
        fprintf(f,"\"velocityjitter\":[%.9g,%.9g,%.9g],",e->velocityjitter.x,e->velocityjitter.y,e->velocityjitter.z);
        fprintf(f,"\"staincolor0\":%d,\"staincolor1\":%d,\"staintex0\":%d,\"staintex1\":%d,",
                (int)e->staincolor[0],(int)e->staincolor[1],e->staintex[0],e->staintex[1]);
        fprintf(f,"\"stainalpha\":[%.9g,%.9g],\"stainsize\":[%.9g,%.9g],",e->stainalpha[0],e->stainalpha[1],e->stainsize[0],e->stainsize[1]);
        fprintf(f,"\"rotate\":[%.9g,%.9g,%.9g,%.9g],",e->rotate[0],e->rotate[1],e->rotate[2],e->rotate[3]);
        fprintf(f,"\"underwater\":%d,\"notunderwater\":%d",e->underwater,e->notunderwater);
        fprintf(f,"}%s\n", bi+1<s->nblocks?",":"");
    }
    fprintf(f,"  ],\n");

    fprintf(f,"  \"spawn\": { \"pcount\": %.9g, \"fade\": %.9g, \"wanttrail\": %d,\n", s->pcount, s->fade, s->wanttrail);
    fprintf(f,"    \"originmins\": [%.9g,%.9g,%.9g], \"originmaxs\": [%.9g,%.9g,%.9g],\n",
            s->originmins.x,s->originmins.y,s->originmins.z,s->originmaxs.x,s->originmaxs.y,s->originmaxs.z);
    fprintf(f,"    \"velocitymins\": [%.9g,%.9g,%.9g], \"velocitymaxs\": [%.9g,%.9g,%.9g] },\n",
            s->velocitymins.x,s->velocitymins.y,s->velocitymins.z,s->velocitymaxs.x,s->velocitymaxs.y,s->velocitymaxs.z);

    /* the recorded RNG sequence (all draws: spawn + every update step). */
    fprintf(f,"  \"rng\": [");
    int total = g_rng_count; /* spawn-only so far; updates will add more below — but C# replays ALL draws */
    (void)total;

    /* We must emit the full rng stream the C# will consume: spawn + all updates. So run the
       updates first (recording), THEN emit. Capture the post-spawn particle state too. We do
       this by buffering step output strings. To keep it simple we accumulate step JSON into a
       memory buffer while running, then print rng (now complete) followed by the buffer. */

    /* run updates, buffering step JSON */
    char *buf = (char*)malloc(8*1024*1024);
    size_t blen=0;
    blen += (size_t)sprintf(buf+blen, "  \"steps\": [\n");
    float t = 0;
    /* step 0: state right after spawn (no update yet) */
    for (int st=0; st<s->nsteps; st++){
        t += s->dts[st];
        update_particles(t);
        blen += (size_t)sprintf(buf+blen, "    { \"dt\": %.9g, \"time\": %.9g, \"particles\": [", s->dts[st], t);
        int first=1;
        for (int i=0;i<g_num_particles;i++){
            particle_t *p=&parts[i];
            blen += (size_t)sprintf(buf+blen, "%s[%d,%.9g,%.9g,%.9g,%.9g,%.9g,%.9g,%.9g,%.9g]",
                first?"":",",
                p->typeindex? (p->delayedspawn> g_time?0:1) : 0,
                p->org.x,p->org.y,p->org.z, p->vel.x,p->vel.y,p->vel.z, p->size, p->alpha);
            first=0;
        }
        blen += (size_t)sprintf(buf+blen, "] }%s\n", st+1<s->nsteps?",":"");
    }
    blen += (size_t)sprintf(buf+blen, "  ]\n");
    buf[blen]=0;

    /* now g_rng_count is complete; emit the full stream */
    for (int i=0;i<g_rng_count;i++)
        fprintf(f,"%s%d", i?",":"", g_rng[i]);
    fprintf(f,"],\n");
    fprintf(f,"  \"spawn_rng_count\": %d,\n", spawn_rng);

    fprintf(f,"%s", buf);
    free(buf);
    fprintf(f,"}\n");
}

/* --- world builders --- */
static cworld empty_world(void){ cworld w; w.nbrushes=0; return w; }
static cworld floor_world(void){
    /* solid floor brush at z<=0 (top plane z=0), large in xy. */
    cworld w; w.nbrushes=0;
    w.brushes[w.nbrushes++] = box_brush(v3(-8192,-8192,-512), v3(8192,8192,0), SC_SOLID, 0);
    return w;
}
static cworld wall_world(void){
    /* solid wall: x>=64 is solid (face plane x=64). */
    cworld w; w.nbrushes=0;
    w.brushes[w.nbrushes++] = box_brush(v3(64,-8192,-8192), v3(8192,8192,8192), SC_SOLID, 0);
    return w;
}
static cworld water_world(void){
    /* solid floor at z<=-64, water box from z=-64..64. */
    cworld w; w.nbrushes=0;
    w.brushes[w.nbrushes++] = box_brush(v3(-8192,-8192,-512), v3(8192,8192,-64), SC_SOLID, 0);
    w.brushes[w.nbrushes++] = box_brush(v3(-512,-512,-64), v3(512,512,64), SC_WATER, 0);
    return w;
}

int main(int argc, char **argv){
    const char *outdir = argc>1 ? argv[1] : ".";
    char path[1024];
    const float DT16 = 0.0166f, DT7 = 0.007f, DT41 = 0.041f;

    /* 1) jittered_burst — alphastatic smoke-ish point burst with origin+velocity jitter, no gravity/collision */
    {
        scenario s; memset(&s,0,sizeof(s));
        s.name="jittered_burst"; s.desc="point burst, origin+velocity jitter, no gravity, no collision";
        s.world=empty_world(); s.collisions=0;
        einfo e=baseline_einfo();
        e.particletype=pt_alphastatic; e.countabsolute=12; e.countmultiplier=0;
        e.color[0]=0x808080; e.color[1]=0xFFFFFF; e.tex[0]=0; e.tex[1]=8;
        e.size[0]=3; e.size[1]=5; e.size[2]=4;
        e.alpha[0]=128; e.alpha[1]=256; e.alpha[2]=128;
        e.time[0]=2; e.time[1]=3;
        e.originjitter=v3(8,8,8); e.velocityjitter=v3(40,40,40);
        e.airfriction=2;
        s.blocks[0]=e; s.nblocks=1;
        s.pcount=1; s.fade=1; s.wanttrail=0;
        s.originmins=v3(0,0,32); s.originmaxs=v3(0,0,32);
        s.velocitymins=v3(0,0,0); s.velocitymaxs=v3(0,0,0);
        s.nsteps=20; for(int i=0;i<s.nsteps;i++) s.dts[i] = (i%3==0)?DT41:(i%3==1)?DT7:DT16;
        snprintf(path,sizeof(path),"%s/jittered_burst.json",outdir);
        FILE *f=fopen(path,"wb"); emit_json(f,&s); fclose(f);
    }

    /* 2) gravity_spark_bounce_plane — sparks with gravity bouncing off the floor */
    {
        scenario s; memset(&s,0,sizeof(s));
        s.name="gravity_spark_bounce_plane"; s.desc="sparks, gravity, bounce off floor plane z=0";
        s.world=floor_world(); s.collisions=1;
        einfo e=baseline_einfo();
        e.particletype=pt_spark; e.orientation=PARTICLE_SPARK; e.blendmode=PBLEND_ADD;
        e.countabsolute=8; e.color[0]=0xFFD060; e.color[1]=0xFFFFFF; e.tex[0]=63; e.tex[1]=63;
        e.size[0]=1; e.size[1]=1; e.size[2]=0;
        e.alpha[0]=256; e.alpha[1]=256; e.alpha[2]=512;
        e.time[0]=1; e.time[1]=2;
        e.gravity=1; e.bounce=1.5f; e.airfriction=1; e.stretchfactor=2;
        e.velocityjitter=v3(48,48,48); e.velocitymultiplier=1;
        s.blocks[0]=e; s.nblocks=1;
        s.pcount=1; s.fade=1; s.wanttrail=0;
        s.originmins=v3(0,0,48); s.originmaxs=v3(0,0,48);
        s.velocitymins=v3(0,0,80); s.velocitymaxs=v3(0,0,80);
        s.nsteps=40; for(int i=0;i<s.nsteps;i++) s.dts[i]=DT16;
        snprintf(path,sizeof(path),"%s/gravity_spark_bounce_plane.json",outdir);
        FILE *f=fopen(path,"wb"); emit_json(f,&s); fclose(f);
    }

    /* 3) liquid_pool — bubbles inside a water box: liquidfriction + buoyancy(up gravity) */
    {
        scenario s; memset(&s,0,sizeof(s));
        s.name="liquid_pool"; s.desc="bubbles inside water box, liquidfriction + upward drift";
        s.world=water_world(); s.collisions=1;
        einfo e=baseline_einfo();
        e.particletype=pt_bubble; e.countabsolute=6; e.color[0]=0x404060; e.color[1]=0x8080FF;
        e.tex[0]=62; e.tex[1]=63;
        e.size[0]=2; e.size[1]=3; e.size[2]=0;
        e.alpha[0]=256; e.alpha[1]=256; e.alpha[2]=64;
        e.time[0]=4; e.time[1]=5;
        e.gravity=-0.25f; e.liquidfriction=4; e.velocityjitter=v3(16,16,16);
        s.blocks[0]=e; s.nblocks=1;
        s.pcount=1; s.fade=1; s.wanttrail=0;
        s.originmins=v3(0,0,0); s.originmaxs=v3(0,0,0);
        s.velocitymins=v3(0,0,0); s.velocitymaxs=v3(0,0,0);
        s.nsteps=30; for(int i=0;i<s.nsteps;i++) s.dts[i]=DT16;
        snprintf(path,sizeof(path),"%s/liquid_pool.json",outdir);
        FILE *f=fopen(path,"wb"); emit_json(f,&s); fclose(f);
    }

    /* 4) trail_sweep — smoke trail from originmins->originmaxs (trailspacing>0, wanttrail) */
    {
        scenario s; memset(&s,0,sizeof(s));
        s.name="trail_sweep"; s.desc="smoke trail along a segment, trailspacing, no collision";
        s.world=empty_world(); s.collisions=0;
        einfo e=baseline_einfo();
        e.particletype=pt_smoke; e.blendmode=PBLEND_ALPHA; e.trailspacing=16;
        e.color[0]=0x303030; e.color[1]=0x606060; e.tex[0]=0; e.tex[1]=8;
        e.size[0]=4; e.size[1]=6; e.size[2]=8;
        e.alpha[0]=128; e.alpha[1]=192; e.alpha[2]=64;
        e.time[0]=3; e.time[1]=4;
        e.airfriction=1; e.originjitter=v3(2,2,2); e.velocityjitter=v3(8,8,8);
        e.relativeoriginoffset=v3(4,0,0);
        s.blocks[0]=e; s.nblocks=1;
        s.pcount=1; s.fade=1; s.wanttrail=1;
        s.originmins=v3(0,0,64); s.originmaxs=v3(128,0,64);
        s.velocitymins=v3(0,0,0); s.velocitymaxs=v3(0,0,0);
        s.nsteps=24; for(int i=0;i<s.nsteps;i++) s.dts[i]=(i%2==0)?DT16:DT7;
        snprintf(path,sizeof(path),"%s/trail_sweep.json",outdir);
        FILE *f=fopen(path,"wb"); emit_json(f,&s); fclose(f);
    }

    /* 5) snow — snow particles with flutter timer (rand&3 + x-into-y bug), no collision */
    {
        scenario s; memset(&s,0,sizeof(s));
        s.name="snow"; s.desc="snow with flutter timer + DP x-into-y vel bug, no collision";
        s.world=empty_world(); s.collisions=0;
        einfo e=baseline_einfo();
        e.particletype=pt_snow; e.countabsolute=8; e.color[0]=0xFFFFFF; e.color[1]=0xFFFFFF;
        e.tex[0]=24; e.tex[1]=32;
        e.size[0]=1; e.size[1]=2; e.size[2]=0;
        e.alpha[0]=256; e.alpha[1]=256; e.alpha[2]=0;   /* alphafade 0 -> lifetime drives die */
        e.time[0]=8; e.time[1]=10;
        e.gravity=0; e.velocityjitter=v3(16,16,0); e.velocitymultiplier=1;
        s.blocks[0]=e; s.nblocks=1;
        s.pcount=1; s.fade=1; s.wanttrail=0;
        s.originmins=v3(-16,-16,128); s.originmaxs=v3(16,16,128);
        s.velocitymins=v3(0,0,-40); s.velocitymaxs=v3(0,0,-40);
        s.nsteps=30; for(int i=0;i<s.nsteps;i++) s.dts[i]=DT16;
        snprintf(path,sizeof(path),"%s/snow.json",outdir);
        FILE *f=fopen(path,"wb"); emit_json(f,&s); fclose(f);
    }

    /* 6) blood_vs_wall — blood with bounce>0 thrown at a solid wall (x=64); killed on impact */
    {
        scenario s; memset(&s,0,sizeof(s));
        s.name="blood_vs_wall"; s.desc="blood, gravity, thrown +x into wall x=64, killed on solid impact";
        s.world=wall_world(); s.collisions=1;
        einfo e=baseline_einfo();
        e.particletype=pt_blood; e.blendmode=PBLEND_INVMOD; e.countabsolute=6;
        e.color[0]=0xA01010; e.color[1]=0x600808; e.tex[0]=24; e.tex[1]=32;
        e.size[0]=4; e.size[1]=6; e.size[2]=8;
        e.alpha[0]=256; e.alpha[1]=256; e.alpha[2]=64;
        e.time[0]=4; e.time[1]=6;
        e.gravity=1; e.bounce=-1; e.airfriction=1; e.velocityjitter=v3(16,16,16);
        e.velocitymultiplier=1;   /* pass the spawn velocity through (baseline default is 0) */
        e.staintex[0]=-1; e.staintex[1]=-1;
        s.blocks[0]=e; s.nblocks=1;
        s.pcount=1; s.fade=1; s.wanttrail=0;
        s.originmins=v3(0,0,32); s.originmaxs=v3(0,0,32);
        s.velocitymins=v3(300,0,0); s.velocitymaxs=v3(300,0,0);
        s.nsteps=24; for(int i=0;i<s.nsteps;i++) s.dts[i]=DT16;
        snprintf(path,sizeof(path),"%s/blood_vs_wall.json",outdir);
        FILE *f=fopen(path,"wb"); emit_json(f,&s); fclose(f);
    }

    printf("wrote 6 golden particle scenarios to %s (rng draws vary per scenario)\n", outdir);
    return 0;
}
