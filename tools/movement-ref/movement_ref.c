/*
 * movement_ref.c — Darkplaces/Xonotic player-movement GOLDEN-TRACE generator.
 *
 * This is an INDEPENDENT C reference for the Xonotic player physics, translated
 * DIRECTLY and line-for-line from the *preprocessed* QuakeC the engine actually
 * runs:
 *
 *   Base/data/xonotic-data.pk3dir/.tmp/server.txt   (gmqcc -E output; the exact
 *   source that was compiled into progs.dat — autocvars inlined, macros expanded)
 *
 *   * sys_phys_update / sys_phys_simulate          (ecs/systems/physics.qc)
 *   * PM_Accelerate / CPM_PM_Aircontrol /
 *     AdjustAirAccelQW / IsMoveInDirection /
 *     GeomLerp / PM_AirAccelerate / PlayerJump      (common/physics/player.qc)
 *   * _Movetype_FlyMove / _Movetype_Physics_Walk /
 *     _Movetype_PushEntity / _Movetype_ClipVelocity /
 *     _Movetype_CheckWater                          (common/physics/movetypes/*)
 *
 * It is deliberately NOT derived from the C# port (src/Rebirth.Common/Physics):
 * the whole point of a golden corpus is an *independent* implementation to A/B
 * the port against. Because QuakeC float arithmetic is IEEE-754 single precision,
 * and this file is single precision throughout, the two agree to within
 * transcendental ULP noise on the analytic test worlds defined below.
 *
 * Movement cvars are the stock Xonotic set (Base/.../physicsX.cfg, exec'd by
 * xonotic-server.cfg), verified field-by-field against MovementParameters.Defaults.
 *
 * The collision world is a small set of convex brushes (axis-aligned floor/steps +
 * an inclined ramp + a water volume). The brush-vs-box trace below is reproduced
 * VERBATIM in C# (Rebirth.Tests AnalyticTraceService), so collision is identical on
 * both sides by construction and any trajectory divergence is a pure physics-math
 * difference — exactly what the parity test must catch.
 *
 * Output: one JSON file per scenario on stdout-redirect (see main()), holding the
 * world, the start state, and a per-tick {input, expected output} list.
 *
 * Build:  gcc-12 -O2 -std=c11 -o movement_ref movement_ref.c -lm
 * Run:    ./movement_ref <output-dir>
 */

#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <math.h>
#include <stdbool.h>

/* ===================================================================== *
 *  vector math (Quake semantics: float3, '*' between vectors == dot)
 * ===================================================================== */
typedef struct { float x, y, z; } qvec;

static inline qvec v3(float x, float y, float z){ qvec r={x,y,z}; return r; }
static inline qvec vadd(qvec a, qvec b){ return v3(a.x+b.x,a.y+b.y,a.z+b.z); }
static inline qvec vsub(qvec a, qvec b){ return v3(a.x-b.x,a.y-b.y,a.z-b.z); }
static inline qvec vscale(qvec a, float s){ return v3(a.x*s,a.y*s,a.z*s); }
static inline float vdot(qvec a, qvec b){ return a.x*b.x+a.y*b.y+a.z*b.z; }
static inline qvec vcross(qvec a, qvec b){ return v3(a.y*b.z-a.z*b.y, a.z*b.x-a.x*b.z, a.x*b.y-a.y*b.x); }
static inline float vlen(qvec a){ return sqrtf(vdot(a,a)); }
static inline bool vzero(qvec a){ return a.x==0.0f && a.y==0.0f && a.z==0.0f; }
static inline qvec vec2(qvec a){ return v3(a.x,a.y,0.0f); } /* QC vec2() / drop z */
/* QC normalize(): zero-vector -> zero (NOT NaN) */
static inline qvec vnorm(qvec a){ float l=vlen(a); return l>0.0f ? vscale(a,1.0f/l) : v3(0,0,0); }

static inline float fbound(float lo, float v, float hi){ return v<lo?lo:(v>hi?hi:v); }

#define DEG2RAD 0.0174532925199432957692f
#define RAD2DEG 57.295779513082320877f

/* makevectors(): DP AngleVectors (mathlib.c). pitch=X (down-positive), yaw=Y, roll=Z */
static void makevectors(qvec ang, qvec *fwd, qvec *right, qvec *up){
    float ay=ang.y*DEG2RAD, sy=sinf(ay), cy=cosf(ay);
    float ap=ang.x*DEG2RAD, sp=sinf(ap), cp=cosf(ap);
    float ar=ang.z*DEG2RAD, sr=sinf(ar), cr=cosf(ar);
    *fwd  = v3(cp*cy, cp*sy, -sp);
    *right= v3(-sr*sp*cy + cr*sy, -sr*sp*sy - cr*cy, -sr*cp);
    *up   = v3( cr*sp*cy + sr*sy,  cr*sp*sy - sr*cy,  cr*cp);
}

/* ===================================================================== *
 *  Movement parameters — stock Xonotic (physicsX.cfg), verified vs port
 * ===================================================================== */
typedef struct {
    float maxspeed, accelerate, friction, friction_slick, stopspeed, slickaccelerate, friction_on_land;
    float maxairspeed, airaccelerate, airaccel_qw, airstrafeaccel_qw, airaccel_qw_stretchfactor;
    float airspeedlimit_nonqw, airaccel_sideways_friction, maxairstrafespeed, airstrafeaccelerate;
    float airstopaccelerate; int airstopaccelerate_full;
    float aircontrol; int aircontrol_flags; float aircontrol_power, aircontrol_penalty;
    float warsowbunny_turnaccel, warsowbunny_airforwardaccel, warsowbunny_topspeed, warsowbunny_accel, warsowbunny_backtosideratio;
    float jumpvelocity, jumpvelocity_crouch, jumpspeedcap_min, jumpspeedcap_max; int jumpspeedcap_max_disable_on_ramps;
    int track_canjump, doublejump, jumpstep;
    float gravity, stepheight; int stepdown; float stepdown_maxspeed; int wallfriction;
    qvec player_mins, player_maxs;
} mparams;

static mparams stock(void){
    mparams p;
    p.maxspeed=360; p.accelerate=15; p.friction=6; p.friction_slick=0.5f; p.stopspeed=100;
    p.slickaccelerate=15; p.friction_on_land=0;
    p.maxairspeed=360; p.airaccelerate=2; p.airaccel_qw=-0.8f; p.airstrafeaccel_qw=-0.95f;
    p.airaccel_qw_stretchfactor=2; p.airspeedlimit_nonqw=900; p.airaccel_sideways_friction=0;
    p.maxairstrafespeed=100; p.airstrafeaccelerate=18; p.airstopaccelerate=3; p.airstopaccelerate_full=0;
    p.aircontrol=100; p.aircontrol_flags=0; p.aircontrol_power=2; p.aircontrol_penalty=0;
    p.warsowbunny_turnaccel=0; p.warsowbunny_airforwardaccel=1.00001f; p.warsowbunny_topspeed=925;
    p.warsowbunny_accel=0.1593f; p.warsowbunny_backtosideratio=0.8f;
    p.jumpvelocity=260; p.jumpvelocity_crouch=0; p.jumpspeedcap_min=NAN; p.jumpspeedcap_max=NAN;
    p.jumpspeedcap_max_disable_on_ramps=1; p.track_canjump=0; p.doublejump=0; p.jumpstep=1;
    p.gravity=800; p.stepheight=31; p.stepdown=2; p.stepdown_maxspeed=400; p.wallfriction=1; /* DP engine default; field is unread (see WalkMove note) — wall friction is a no-op in stock QC */
    p.player_mins=v3(-16,-16,-24); p.player_maxs=v3(16,16,45);
    return p;
}

/* engine gameplayfix flags (autocvar defaults, verified in server.txt) */
#define GF_GRAVITYUNAFFECTEDBYTICRATE 1
#define GF_NOGRAVITYONGROUND          1
#define GF_STEPMULTIPLETIMES          1
#define GF_DOWNTRACEONGROUND          1
/* LIVE Base runs the q2airaccelerate fix ON: QC autocvar inline default 1 (stats.qh:396),
 * xonotic-server.cfg:562, replicated to CSQC via MOVEFLAG_Q2AIRACCELERATE (stats.qh:401).
 * player.qc:288-289 applies it INSIDE PM_Accelerate — AFTER the strafe GeomLerp / duck clamps
 * shrank wishspeed — so the air accel step is accel*dt*wishspeed (pure strafe: 100), NOT
 * accel*dt*wishspeed0 (350). The old 0 here silently ~tripled mid-air strafe accel; the
 * verify-against-dp.md §2.2 "benign" verdict only checked the call-site equality and missed the
 * in-between strafe clamp. Straight-line scenarios are unaffected (wishspeed == wishspeed0). */
#define GF_Q2AIRACCELERATE            1

#define MAX_CLIP_PLANES 5
#define ONGROUND_NORMAL_Z 0.7f

/* SUPERCONTENTS (bspfile.h) */
#define CONT_SOLID 0x00000001
#define CONT_WATER 0x00000010
#define CONT_SLIME 0x00000020
#define CONT_LAVA  0x00000040
#define CONT_LIQUIDSMASK (CONT_WATER|CONT_SLIME|CONT_LAVA)

/* ===================================================================== *
 *  Collision world + brush-vs-box trace (mirrored verbatim in C#)
 * ===================================================================== */
typedef struct { qvec normal; float dist; } cplane;      /* inside: dot(normal,p) <= dist */
typedef struct { cplane planes[8]; int nplanes; int contents; } cbrush;
typedef struct { cbrush brushes[16]; int nbrushes; } cworld;

typedef struct {
    float fraction; qvec endpos; qvec plane_normal; int startsolid; int allsolid; int hitcontents;
} ctrace;

/* axis-aligned box brush */
static cbrush box_brush(qvec mins, qvec maxs, int contents){
    cbrush b; b.nplanes=0; b.contents=contents;
    b.planes[b.nplanes++] = (cplane){ v3( 1,0,0),  maxs.x };
    b.planes[b.nplanes++] = (cplane){ v3(-1,0,0), -mins.x };
    b.planes[b.nplanes++] = (cplane){ v3(0, 1,0),  maxs.y };
    b.planes[b.nplanes++] = (cplane){ v3(0,-1,0), -mins.y };
    b.planes[b.nplanes++] = (cplane){ v3(0,0, 1),  maxs.z };
    b.planes[b.nplanes++] = (cplane){ v3(0,0,-1), -mins.z };
    return b;
}

#define TRACE_DIST_EPSILON (1.0f/32.0f)

/* clip the swept point-trace [start->end] of a box hull [mins,maxs] against one
 * convex brush.  Faithful to Quake CM_ClipBoxToBrush / DP Collision_TraceBrushBrush.
 * Each plane is expanded by the hull support so the origin can be treated as a point. */
static void clip_box_to_brush(ctrace *tr, qvec start, qvec end, qvec mins, qvec maxs, const cbrush *b){
    if(b->nplanes==0) return;
    float enterfrac = -1.0f, leavefrac = 1.0f;
    qvec clipnormal = v3(0,0,0);
    bool startout=false, endout=false;
    for(int i=0;i<b->nplanes;i++){
        qvec n = b->planes[i].normal;
        /* expand: d' = d - sum_i (n[i]>0 ? n[i]*mins[i] : n[i]*maxs[i]) */
        float d = b->planes[i].dist;
        d -= (n.x>0?n.x*mins.x:n.x*maxs.x);
        d -= (n.y>0?n.y*mins.y:n.y*maxs.y);
        d -= (n.z>0?n.z*mins.z:n.z*maxs.z);
        float d1 = vdot(n,start) - d;
        float d2 = vdot(n,end)   - d;
        if(d1>0) startout=true;
        if(d2>0) endout=true;
        if(d1>0 && d2>=0) return;          /* wholly outside this plane -> no hit */
        if(d1<=0 && d2<=0) continue;        /* wholly inside this plane's halfspace */
        if(d1>d2){                          /* entering */
            float f=(d1-TRACE_DIST_EPSILON)/(d1-d2);
            if(f>enterfrac){ enterfrac=f; clipnormal=n; }
        } else {                            /* leaving */
            float f=(d1+TRACE_DIST_EPSILON)/(d1-d2);
            if(f<leavefrac) leavefrac=f;
        }
    }
    if(!startout){                          /* began inside the brush */
        tr->startsolid=1;
        if(!endout) tr->allsolid=1;
        if(b->contents & CONT_SOLID){ tr->hitcontents |= b->contents; }
        return;
    }
    if(enterfrac < leavefrac && enterfrac > -1.0f){
        if(enterfrac < 0.0f) enterfrac = 0.0f;
        if(enterfrac < tr->fraction){
            tr->fraction = enterfrac;
            tr->plane_normal = clipnormal;
            tr->hitcontents = b->contents;
        }
    }
}

/* tracebox(start, mins, maxs, end) vs the world's SOLID brushes */
static ctrace world_trace(const cworld *w, qvec start, qvec mins, qvec maxs, qvec end){
    ctrace tr; tr.fraction=1.0f; tr.endpos=end; tr.plane_normal=v3(0,0,0);
    tr.startsolid=0; tr.allsolid=0; tr.hitcontents=0;
    for(int i=0;i<w->nbrushes;i++){
        if(!(w->brushes[i].contents & CONT_SOLID)) continue; /* solids only block */
        clip_box_to_brush(&tr, start, end, mins, maxs, &w->brushes[i]);
        if(tr.allsolid) tr.fraction=0.0f;
    }
    if(tr.startsolid) tr.fraction = tr.allsolid ? 0.0f : tr.fraction;
    tr.endpos = vadd(start, vscale(vsub(end,start), tr.fraction));
    return tr;
}

/* pointcontents(): OR of the contents of every brush containing the point */
static int world_pointcontents(const cworld *w, qvec p){
    int c=0;
    for(int i=0;i<w->nbrushes;i++){
        const cbrush *b=&w->brushes[i];
        bool inside=true;
        for(int j=0;j<b->nplanes;j++)
            if(vdot(b->planes[j].normal,p) - b->planes[j].dist > 0){ inside=false; break; }
        if(inside) c |= b->contents;
    }
    return c;
}

/* ===================================================================== *
 *  Player entity (the subset the movement touches)
 * ===================================================================== */
enum { FL_ONGROUND=1, FL_JUMPRELEASED=2, FL_DUCKED=4, FL_ONSLICK=8, FL_WATERJUMP=16 };
enum { WL_NONE=0, WL_WETFEET=1, WL_SWIMMING=2, WL_SUBMERGED=3 };

typedef struct {
    qvec origin, velocity, v_angle, mins, maxs;
    int flags, lastflags, waterlevel, watertype;
    float lastground, gravity;
    int onground; /* convenience mirror of flags&FL_ONGROUND for groundentity != NULL */
} pent;

typedef struct {
    qvec angles, move; int jump, crouch; float dt;
} pinput;

static const cworld *g_world;
static float g_time;

/* ===================================================================== *
 *  PM accel math (player.qc, preprocessed)
 * ===================================================================== */
static float AdjustAirAccelQW(float accelqw, float factor){
    return copysignf(fbound(0.000001f, 1.0f - (1.0f - fabsf(accelqw))*factor, 1.0f), accelqw);
}

static float IsMoveInDirection(qvec mv, float ang){
    if(mv.x==0.0f && mv.y==0.0f) return 0.0f;
    ang -= RAD2DEG * atan2f(mv.y, mv.x);
    ang = remainderf(ang, 360.0f) / 45.0f;
    if(ang > 1.0f) return 0.0f;
    if(ang < -1.0f) return 0.0f;
    return 1.0f - fabsf(ang);
}

static float GeomLerp(float a, float lerp, float b){
    if(a==0.0f) return lerp<1.0f ? 0.0f : b;
    if(b==0.0f) return lerp>0.0f ? 0.0f : a;
    return a * powf(fabsf(b/a), lerp);
}

static void PM_Accelerate(pent *e, float dt, qvec wishdir, float wishspeed, float wishspeed0,
                          float accel, float accelqw, float stretchfactor, float sidefric, float speedlimit){
    float speedclamp = stretchfactor>0 ? stretchfactor : (accelqw<0 ? 1.0f : -1.0f);
    accelqw = fabsf(accelqw);
    if(GF_Q2AIRACCELERATE) wishspeed0 = wishspeed;

    float vel_straight = vdot(e->velocity, wishdir);
    float vel_z = e->velocity.z;
    qvec vel_xy = vec2(e->velocity);
    qvec vel_perpend = vsub(vel_xy, vscale(wishdir, vel_straight));

    float step = accel*dt*wishspeed0;

    float vel_xy_current = vlen(vel_xy);
    if(speedlimit)
        accelqw = AdjustAirAccelQW(accelqw, (speedlimit - fbound(wishspeed, vel_xy_current, speedlimit)) / fmaxf(1.0f, speedlimit - wishspeed));
    float vel_xy_forward  = vel_xy_current + fbound(0, wishspeed - vel_xy_current, step)*accelqw + step*(1.0f-accelqw);
    float vel_xy_backward = vel_xy_current - fbound(0, wishspeed + vel_xy_current, step)*accelqw - step*(1.0f-accelqw);
    if(vel_xy_backward < 0) vel_xy_backward = 0;
    vel_straight = vel_straight + fbound(0, wishspeed - vel_straight, step)*accelqw + step*(1.0f-accelqw);

    if(sidefric<0 && vdot(vel_perpend,vel_perpend)){
        float f = fmaxf(0.0f, 1.0f + dt*wishspeed*sidefric);
        float themin = (vel_xy_backward*vel_xy_backward - vel_straight*vel_straight) / vdot(vel_perpend,vel_perpend);
        if(themin<=0) vel_perpend = vscale(vel_perpend, f);
        else { themin=sqrtf(themin); vel_perpend = vscale(vel_perpend, fmaxf(themin,f)); }
    } else {
        vel_perpend = vscale(vel_perpend, fmaxf(0.0f, 1.0f - dt*wishspeed*sidefric));
    }

    vel_xy = vadd(vscale(wishdir, vel_straight), vel_perpend);

    if(speedclamp>=0){
        float vel_xy_preclamp = vlen(vel_xy);
        if(vel_xy_preclamp>0){
            vel_xy_current += (vel_xy_forward - vel_xy_current)*speedclamp;
            if(vel_xy_current < vel_xy_preclamp) vel_xy = vscale(vel_xy, vel_xy_current/vel_xy_preclamp);
        }
    }
    e->velocity = vadd(vel_xy, v3(0,0,vel_z));
}

static void CPM_PM_Aircontrol(pent *e, const mparams *mp, qvec move, float dt, qvec wishdir, float wishspeed){
    float movity = IsMoveInDirection(move, 0);
    if(mp->aircontrol_flags & (1<<0)) movity += IsMoveInDirection(move, 180);
    if(mp->aircontrol_flags & (1<<1)){ movity += IsMoveInDirection(move, 90); movity += IsMoveInDirection(move, -90); }

    float k = 2.0f*movity - 1.0f;
    if(k<=0) return;
    if(!(mp->aircontrol_flags & (1<<2)))
        k *= fbound(0.0f, mp->maxairspeed!=0.0f ? wishspeed/mp->maxairspeed : 0.0f, 1.0f);

    float zspeed = e->velocity.z;
    e->velocity.z = 0;
    float xyspeed = vlen(e->velocity);
    e->velocity = vnorm(e->velocity);

    float dot = vdot(e->velocity, wishdir);
    if(dot>0){
        k *= powf(dot, mp->aircontrol_power) * dt;
        xyspeed = fmaxf(0.0f, xyspeed - mp->aircontrol_penalty * sqrtf(fmaxf(0.0f, 1.0f - dot*dot)) * k);
        k *= 32.0f * fabsf(mp->aircontrol);
        e->velocity = vnorm(vadd(vscale(e->velocity, xyspeed), vscale(wishdir, k)));
    }
    e->velocity = vscale(e->velocity, xyspeed);
    e->velocity.z = zspeed;
}

static void PM_AirAccelerate(pent *e, const mparams *mp, float dt, qvec wishdir, float wishspeed){
    if(wishspeed==0.0f) return;
    qvec curvel = e->velocity; curvel.z=0;
    float curspeed = vlen(curvel);
    if(wishspeed > curspeed*1.01f)
        wishspeed = fminf(wishspeed, curspeed + mp->warsowbunny_airforwardaccel*mp->maxspeed*dt);
    else {
        float f = fmaxf(0.0f, (mp->warsowbunny_topspeed - curspeed)/(mp->warsowbunny_topspeed - mp->maxspeed));
        wishspeed = fmaxf(curspeed, mp->maxspeed) + mp->warsowbunny_accel*f*mp->maxspeed*dt;
    }
    qvec wishvel = vscale(wishdir, wishspeed);
    qvec acceldir = vsub(wishvel, curvel);
    float addspeed = vlen(acceldir);
    acceldir = vnorm(acceldir);
    float accelspeed = fminf(addspeed, mp->warsowbunny_turnaccel*mp->maxspeed*dt);
    if(mp->warsowbunny_backtosideratio < 1.0f){
        qvec curdir = vnorm(curvel);
        float dot = vdot(acceldir, curdir);
        if(dot<0) acceldir = vsub(acceldir, vscale(curdir, (1.0f-mp->warsowbunny_backtosideratio)*dot));
    }
    e->velocity = vadd(e->velocity, vscale(acceldir, accelspeed));
}

/* ===================================================================== *
 *  Integration: _Movetype_ClipVelocity / _Movetype_PushEntity / FlyMove / Walk
 * ===================================================================== */
static qvec ClipVelocity(qvec vel, qvec normal, float overbounce){
    vel = vsub(vel, vscale(normal, vdot(vel,normal)*overbounce));
    if(vel.x > -0.1f && vel.x < 0.1f) vel.x=0;
    if(vel.y > -0.1f && vel.y < 0.1f) vel.y=0;
    if(vel.z > -0.1f && vel.z < 0.1f) vel.z=0;
    return vel;
}

static ctrace g_lasttrace;

/* SV_PushEntity: sweep, move to endpoint, (no touch entities in the analytic world). */
static int PushEntity(pent *e, qvec push){
    qvec start = e->origin;
    qvec end = vadd(start, push);
    ctrace tr = world_trace(g_world, start, e->mins, e->maxs, end);
    if(tr.startsolid){
        /* retry world-only (same world here); if still stuck, report blocked, fraction 0 */
        ctrace wtr = world_trace(g_world, start, e->mins, e->maxs, end);
        if(wtr.startsolid){ tr.fraction=0.0f; g_lasttrace=tr; return 1; }
        tr = wtr;
    }
    e->origin = tr.endpos;
    g_lasttrace = tr;
    return 1; /* never teleported in these scenarios */
}

static qvec move_stepnormal;

static int FlyMove(pent *e, const mparams *mp, float dt, int applygravity, int applystepnormal, float stepheight){
    move_stepnormal = v3(0,0,0);
    if(dt<=0) return 0;
    int blockedflag=0, numplanes=0;
    float time_left=dt, grav=0;
    qvec planes[MAX_CLIP_PLANES];
    for(int j=0;j<MAX_CLIP_PLANES;j++) planes[j]=v3(0,0,0);
    qvec restore_velocity = e->velocity;

    if(applygravity){
        grav = dt * (e->gravity?e->gravity:1.0f) * mp->gravity;
        if(!GF_NOGRAVITYONGROUND || !(e->flags&FL_ONGROUND)){
            if(GF_GRAVITYUNAFFECTEDBYTICRATE) e->velocity.z -= grav*0.5f;
            else e->velocity.z -= grav;
        }
    }
    qvec original_velocity = e->velocity, primal_velocity = e->velocity;

    for(int bump=0; bump<MAX_CLIP_PLANES; bump++){
        if(vzero(e->velocity)) break;
        qvec push = vscale(e->velocity, time_left);
        if(!PushEntity(e, push)){ blockedflag|=8; break; }
        ctrace tr = g_lasttrace;
        if(tr.startsolid && tr.allsolid){ e->velocity = restore_velocity; return 3; }
        if(tr.fraction==1.0f) break;
        time_left *= 1.0f - tr.fraction;
        float my_frac = tr.fraction;
        qvec my_normal = tr.plane_normal;

        if(my_normal.z != 0.0f){
            if(my_normal.z > ONGROUND_NORMAL_Z){ blockedflag|=1; e->flags|=FL_ONGROUND; }
        } else if(stepheight){
            qvec org = e->origin;
            qvec steppush = v3(0,0,stepheight);
            push = vscale(e->velocity, time_left);
            if(!PushEntity(e, steppush)){ blockedflag|=8; break; }
            if(!PushEntity(e, push)){ blockedflag|=8; break; }
            float trace2_fraction = g_lasttrace.fraction;
            steppush = v3(0,0, org.z - e->origin.z);
            if(!PushEntity(e, steppush)){ blockedflag|=8; break; }
            if(e->origin.x - org.x || e->origin.y - org.y){
                time_left *= 1.0f - trace2_fraction;
                numplanes=0;
                continue;
            } else {
                e->origin = org;
                blockedflag|=2;
                if(applystepnormal) move_stepnormal = my_normal;
            }
        } else {
            blockedflag|=2;
            if(applystepnormal) move_stepnormal = my_normal;
        }

        if(my_frac >= 0.001f){ original_velocity = e->velocity; numplanes=0; }
        if(numplanes >= MAX_CLIP_PLANES){ e->velocity=v3(0,0,0); return 3; }
        planes[numplanes++] = my_normal;

        qvec new_velocity = v3(0,0,0);
        int plane, newplane;
        for(plane=0; plane<numplanes; plane++){
            new_velocity = ClipVelocity(original_velocity, planes[plane], 1.0f);
            for(newplane=0; newplane<numplanes; newplane++)
                if(newplane!=plane && vdot(new_velocity, planes[newplane])<0) break;
            if(newplane==numplanes) break;
        }
        if(plane != numplanes){
            e->velocity = new_velocity;
        } else {
            if(numplanes!=2){ e->velocity=v3(0,0,0); return 7; }
            qvec dir = vnorm(vcross(planes[0], planes[1]));
            float d = vdot(dir, e->velocity);
            e->velocity = vscale(dir, d);
        }
        if(vdot(e->velocity, primal_velocity) <= 0){ e->velocity=v3(0,0,0); break; }
    }

    if(applygravity && GF_GRAVITYUNAFFECTEDBYTICRATE){
        if(!GF_NOGRAVITYONGROUND || !(e->flags&FL_ONGROUND))
            e->velocity.z -= grav*0.5f;
    }
    return blockedflag;
}

/* _Movetype_Physics_Walk: the slide + stair recovery + step-down (walk.qc) */
static void WalkMove(pent *e, const mparams *mp, float dt, int applygravity){
    int oldonground = (e->flags&FL_ONGROUND)!=0;
    qvec start_origin = e->origin, start_velocity = e->velocity;

    int clip = FlyMove(e, mp, dt, applygravity, 0, GF_STEPMULTIPLETIMES ? mp->stepheight : 0.0f);

    if(GF_DOWNTRACEONGROUND && !(clip&1)){
        qvec up = vadd(e->origin, v3(0,0,1)), down = vsub(e->origin, v3(0,0,1));
        ctrace tr = world_trace(g_world, up, e->mins, e->maxs, down);
        if(tr.fraction<1.0f && tr.plane_normal.z>ONGROUND_NORMAL_Z) clip|=1;
    }
    /* clear-only (walk.qc:57-58): FL_ONGROUND is granted solely by FlyMove's floor collision
     * (which clips velocity.z into the plane) or by stepdown==2 — never by the downtrace, whose
     * clip|=1 merely keeps an existing on-ground state from being cleared. (QC's `else` arm here
     * is the dead sv_wallclip pm_time branch, not a SET_ONGROUND.) */
    if(!(clip&1)) e->flags &= ~FL_ONGROUND;

    if(clip&8) return;
    if(e->flags&FL_WATERJUMP) return;

    qvec originalorigin=e->origin, originalvelocity=e->velocity;
    int originalflags=e->flags;

    if(clip&2){
        if(fabsf(start_velocity.x)<0.03125f && fabsf(start_velocity.y)<0.03125f) return;
        if(!mp->jumpstep) if(!oldonground && e->waterlevel==0) return;

        e->origin = start_origin; e->velocity = start_velocity;
        qvec upmove = v3(0,0,mp->stepheight);
        if(!PushEntity(e, upmove)) return;
        e->velocity.z = 0;
        clip = FlyMove(e, mp, dt, applygravity, 1, 0);
        e->velocity.z += start_velocity.z;
        if(clip&8) return;
        if(clip && fabsf(originalorigin.y-e->origin.y)<0.03125f && fabsf(originalorigin.x-e->origin.x)<0.03125f){
            e->origin=originalorigin; e->velocity=originalvelocity; e->flags=originalflags;
            return;
        }
        /* wallfriction: sv_wallfriction=1 (DP engine default) but stock QC _Movetype_WallFriction's body is
           commented out (a no-op), so the term never affects the trajectory and is intentionally omitted here */
        return;
    }

    /* step-down */
    int stepdown_skip = !mp->stepdown || e->waterlevel>=WL_SUBMERGED || start_velocity.z>=(1.0f/32.0f)
        || !oldonground || (e->flags&FL_ONGROUND)
        || (mp->stepdown_maxspeed && vdot(start_velocity,start_velocity) >= (mp->stepdown_maxspeed*mp->stepdown_maxspeed) && !(e->flags&FL_ONSLICK));
    if(stepdown_skip) return;

    qvec downmove = v3(0,0, -mp->stepheight + start_velocity.z*dt);
    if(!PushEntity(e, downmove)) return;
    ctrace dtr = g_lasttrace;
    if(dtr.fraction<1.0f && dtr.plane_normal.z>ONGROUND_NORMAL_Z){
        if(mp->stepdown==2) e->flags|=FL_ONGROUND;
    } else {
        e->origin=originalorigin; e->velocity=originalvelocity; e->flags=originalflags;
    }
}

/* gravity-free slide for water/ladder/fly branches (Integrate(applyGravity=false)) */
static void Integrate(pent *e, const mparams *mp, float dt, int nocollide){
    if(nocollide){ e->origin = vadd(e->origin, vscale(e->velocity, dt)); return; }
    WalkMove(e, mp, dt, 0);
}

/* ===================================================================== *
 *  _Movetype_CheckWater (sets waterlevel/watertype from pointcontents)
 * ===================================================================== */
static int CheckWater(pent *e){
    qvec point = e->origin; point.z += e->mins.z + 1;
    int nat = world_pointcontents(g_world, point);
    e->waterlevel = WL_NONE; e->watertype = 0; /* CONTENT_EMPTY */
    if(nat & CONT_LIQUIDSMASK){
        e->watertype = nat;
        e->waterlevel = WL_WETFEET;
        point = e->origin; point.z += (e->mins.z + e->maxs.z)*0.5f;
        if(world_pointcontents(g_world, point) & CONT_LIQUIDSMASK){
            e->waterlevel = WL_SWIMMING;
            point = e->origin; point.z += 22; /* default view_ofs.z */
            if(world_pointcontents(g_world, point) & CONT_LIQUIDSMASK)
                e->waterlevel = WL_SUBMERGED;
        }
    }
    return e->waterlevel > 1;
}

/* ===================================================================== *
 *  sys_phys_simulate (ecs/systems/physics.qc) — ground + air branches
 * ===================================================================== */
typedef struct {
    int ground, air, water, ladder, vel_2d, friction_air;
    float friction, vel_max, acc_rate, gravity;
    float acc_rate_air, acc_rate_air_stop, acc_rate_air_strafe, vel_max_air_strafe, vel_max_air;
} comphys;

static void sys_phys_simulate(pent *e, const mparams *mp, comphys *c, float dt, qvec move, int btn_jump, int btn_crouch){
    if(!c->ground && !c->air){
        e->flags &= ~FL_ONGROUND;
        if(c->friction_air>0){
            float grav = -c->gravity;
            e->velocity.z += grav/2.0f;
            e->velocity = vscale(e->velocity, 1.0f - dt*c->friction);
            e->velocity.z += grav/2.0f;
        }
    }

    qvec forward, right, up;
    qvec v_tmp = c->vel_2d ? v3(0, e->v_angle.y, 0) : e->v_angle;
    makevectors(v_tmp, &forward, &right, &up);
    qvec wishvel = vadd(vadd(vscale(forward, move.x), vscale(right, move.y)),
                        v3(0,0, c->vel_2d ? 0 : move.z));
    if(c->water){
        /* scenarios never freeze; frozen-resurface omitted */
        if(btn_crouch) wishvel.z = -mp->maxspeed;
        else if(vzero(wishvel)) wishvel.z = -60;
    }

    float wishspeed = vlen(wishvel);
    qvec wishdir = wishspeed ? vscale(wishvel, 1.0f/wishspeed) : v3(0,0,0);
    wishspeed = fminf(wishspeed, c->vel_max);

    if(c->air){
        float airaccelqw = mp->airaccel_qw;
        float wishspeed0 = wishspeed;
        float maxairspd = c->vel_max;
        wishspeed = fminf(wishspeed, maxairspd);
        if(e->flags&FL_DUCKED) wishspeed *= 0.5f;
        float airaccel = c->acc_rate_air;
        int accelerating = (vdot(e->velocity, wishdir) > 0);
        float wishspeed2 = wishspeed;

        if(mp->airstopaccelerate){
            float dot = vdot(vnorm(vec2(e->velocity)), wishdir);
            if(dot<0){
                if(mp->airstopaccelerate_full) airaccel = c->acc_rate_air_stop;
                else airaccel += (airaccel - c->acc_rate_air_stop)*dot;
            }
        }
        float strafity = IsMoveInDirection(move, -90) + IsMoveInDirection(move, +90);
        if(mp->maxairstrafespeed) wishspeed = fminf(wishspeed, GeomLerp(c->vel_max_air, strafity, c->vel_max_air_strafe));
        if(mp->airstrafeaccelerate) airaccel = GeomLerp(airaccel, strafity, c->acc_rate_air_strafe);
        if(mp->airstrafeaccel_qw)
            airaccelqw = (((strafity>0.5f ? mp->airstrafeaccel_qw : mp->airaccel_qw) >= 0) ? +1.0f : -1.0f)
                * (1.0f - GeomLerp(1.0f - fabsf(mp->airaccel_qw), strafity, 1.0f - fabsf(mp->airstrafeaccel_qw)));

        if(mp->warsowbunny_turnaccel && accelerating && move.y==0 && move.x!=0)
            PM_AirAccelerate(e, mp, dt, wishdir, wishspeed2);
        else {
            float sidefric = maxairspd ? (mp->airaccel_sideways_friction/maxairspd) : 0.0f;
            PM_Accelerate(e, dt, wishdir, wishspeed, wishspeed0, airaccel, airaccelqw,
                          mp->airaccel_qw_stretchfactor, sidefric, mp->airspeedlimit_nonqw);
        }
        if(mp->aircontrol) CPM_PM_Aircontrol(e, mp, move, dt, wishdir, wishspeed2);
    } else {
        if(c->ground && (e->flags&FL_DUCKED)) wishspeed *= 0.5f;
        if(c->water){
            wishspeed *= 0.7f;
            float f = 1.0f - dt*mp->friction;
            e->velocity = vscale(e->velocity, fbound(0,f,1));
            f = wishspeed - vdot(e->velocity, wishdir);
            if(f>0){ float accelspeed = fminf(mp->accelerate*dt*wishspeed, f); e->velocity = vadd(e->velocity, vscale(wishdir, accelspeed)); }
            if(btn_jump){
                if(e->waterlevel >= WL_SUBMERGED) e->velocity.z = mp->maxspeed*0.7f;
                else e->velocity.z = 200;
            }
            PM_Accelerate(e, dt, wishdir, wishspeed, wishspeed, c->acc_rate, 1, 0, 0, 0);
            return;
        }
        if(c->ground){
            float f2 = vdot(vec2(e->velocity), vec2(e->velocity));
            float realfriction = (e->flags&FL_ONSLICK) ? mp->friction_slick : mp->friction;
            if(f2>0 && realfriction>0){
                float fr = sqrtf(f2);
                float S = mp->stopspeed;
                const float dt_r = 0.00390625f;
                float independent_geometric = powf(1.0f - realfriction*dt_r, dt/dt_r);
                float f;
                if(S<fr && fr < S/independent_geometric){
                    e->velocity = vnorm(e->velocity);
                    f = S - S*realfriction*(dt - (dt_r*logf(S/fr))/logf(1.0f - realfriction*dt_r));
                } else if(fr>=S){ f = independent_geometric; }
                else { f = 1.0f - realfriction*dt*S/fr; }
                f = fmaxf(0.0f, f);
                e->velocity = vscale(e->velocity, f);
            }
            float addspeed = wishspeed - vdot(e->velocity, wishdir);
            if(addspeed>0){
                float accel = (e->flags&FL_ONSLICK) ? mp->slickaccelerate : mp->accelerate;
                float accelspeed = fminf(accel*dt*wishspeed, addspeed);
                e->velocity = vadd(e->velocity, vscale(wishdir, accelspeed));
            }
            return;
        }
        PM_Accelerate(e, dt, wishdir, wishspeed, wishspeed, c->acc_rate, 1, 0, 0, 0);
    }
}


/* ===================================================================== *
 *  PlayerJump / CheckPlayerJump (player.qc, preprocessed; trimmed:
 *  no frozen / typing / mutators / jetpack — none occur in the scenarios)
 * ===================================================================== */
static int PlayerJump(pent *e, const mparams *mp, int btn_jump){
    int doublejump = mp->doublejump;
    float mjumpheight = (mp->jumpvelocity_crouch && (e->flags&FL_DUCKED)) ? mp->jumpvelocity_crouch : mp->jumpvelocity;
    int track_jump = mp->track_canjump;

    if(e->waterlevel >= WL_SWIMMING){
        e->velocity.z = mp->maxspeed*0.7f;
        return 1;
    }
    if(!doublejump)
        if(!(e->flags&FL_ONGROUND))
            return !(e->flags&FL_JUMPRELEASED);
    if(mp->track_canjump) track_jump=1;
    if(track_jump) if(!(e->flags&FL_JUMPRELEASED)) return 1;

    if(!isnan(mp->jumpspeedcap_min)){
        float minjumpspeed = mjumpheight*mp->jumpspeedcap_min;
        if(e->velocity.z < minjumpspeed) mjumpheight += minjumpspeed - e->velocity.z;
    }
    if(!isnan(mp->jumpspeedcap_max)){
        ctrace tr = world_trace(g_world, vadd(e->origin,v3(0,0,0.01f)), e->mins, e->maxs, vsub(e->origin,v3(0,0,0.01f)));
        if(!(tr.fraction<1 && tr.plane_normal.z<0.98f && mp->jumpspeedcap_max_disable_on_ramps)){
            float maxjumpspeed = mjumpheight*mp->jumpspeedcap_max;
            if(e->velocity.z > maxjumpspeed) mjumpheight -= e->velocity.z - maxjumpspeed;
        }
    }
    /* landing friction on a jump while airborne (friction_on_land 0 -> no-op) omitted */
    e->velocity.z += mjumpheight;
    e->flags &= ~FL_ONGROUND;
    e->flags &= ~FL_ONSLICK;
    e->flags &= ~FL_JUMPRELEASED;
    (void)btn_jump;
    return 1;
}

static void CheckPlayerJump(pent *e, const mparams *mp, int btn_jump){
    if(btn_jump){
        PlayerJump(e, mp, btn_jump);
        /* no jetpack item -> no thrust */
    }
    if(!btn_jump) e->flags |= FL_JUMPRELEASED;
    /* CheckWaterJump (surface-against-wall) omitted: needs geometry the scenarios lack */
}

/* ===================================================================== *
 *  Move = sys_phys_update branch selection (faithful: persistent onground)
 * ===================================================================== */
static void player_move(pent *e, const mparams *mp, pinput in){
    float dt = in.dt;
    if(dt<=0) return;

    /* hull already set; no crouch in scenarios */
    CheckWater(e);
    qvec move = in.move;

    /* PM_check_slick: no slick surfaces in the scenarios -> clear */
    e->flags &= ~FL_ONSLICK;

    e->v_angle = in.angles;

    CheckPlayerJump(e, mp, in.jump);

    /* QC: branch reads the PERSISTENT FL_ONGROUND flag (no re-detect down-trace) */
    int onground = (e->flags&FL_ONGROUND)!=0;

    comphys c; memset(&c,0,sizeof(c));

    if(e->flags&FL_WATERJUMP){
        /* not exercised */
        WalkMove(e, mp, dt, 1);
    } else if(e->waterlevel >= WL_SWIMMING){
        c.water=1; c.vel_max=mp->maxspeed; c.acc_rate=mp->accelerate;
        sys_phys_simulate(e, mp, &c, dt, move, in.jump, in.crouch);
        Integrate(e, mp, dt, 0);
    } else if(onground && !(e->flags&FL_ONSLICK)){
        c.ground=1; c.vel_2d=1; c.vel_max=mp->maxspeed*1.0f;
        c.gravity = -(mp->gravity)*dt; if(e->gravity) c.gravity *= e->gravity;
        sys_phys_simulate(e, mp, &c, dt, move, in.jump, in.crouch);
        WalkMove(e, mp, dt, 1);
    } else {
        c.air=1; c.vel_2d=1;
        c.acc_rate_air = mp->airaccelerate;
        c.acc_rate_air_stop = mp->airstopaccelerate;
        c.acc_rate_air_strafe = mp->airstrafeaccelerate;
        c.vel_max_air_strafe = mp->maxairstrafespeed;
        c.vel_max_air = mp->maxairspeed;
        c.vel_max = mp->maxairspeed;
        sys_phys_simulate(e, mp, &c, dt, move, in.jump, in.crouch);
        WalkMove(e, mp, dt, 1);
    }

    /* postupdate */
    if(e->flags&FL_ONGROUND) e->lastground = g_time;
    e->lastflags = e->flags;
}

/* ===================================================================== *
 *  Scenario harness + JSON emitter
 * ===================================================================== */
#define MAXTICKS 256
typedef struct {
    const char *name; const char *desc;
    cworld world;
    qvec start_origin, start_velocity, start_vangle; int start_flags;
    int nticks; pinput in[MAXTICKS];
} scenario;

static void emit_json(FILE *f, scenario *s){
    mparams mp = stock();
    pent e; memset(&e,0,sizeof(e));
    e.origin=s->start_origin; e.velocity=s->start_velocity; e.v_angle=s->start_vangle;
    e.mins=mp.player_mins; e.maxs=mp.player_maxs; e.flags=s->start_flags;
    e.gravity=0; e.lastground=0; e.lastflags=s->start_flags;
    g_world=&s->world; g_time=0;

    fprintf(f,"{\n");
    fprintf(f,"  \"name\": \"%s\",\n", s->name);
    fprintf(f,"  \"description\": \"%s\",\n", s->desc);
    fprintf(f,"  \"world\": [\n");
    for(int i=0;i<s->world.nbrushes;i++){
        cbrush *b=&s->world.brushes[i];
        fprintf(f,"    { \"contents\": %d, \"planes\": [", b->contents);
        for(int j=0;j<b->nplanes;j++)
            fprintf(f,"%s[%.9g,%.9g,%.9g,%.9g]", j?",":"", b->planes[j].normal.x,b->planes[j].normal.y,b->planes[j].normal.z,b->planes[j].dist);
        fprintf(f,"] }%s\n", i+1<s->world.nbrushes?",":"");
    }
    fprintf(f,"  ],\n");
    fprintf(f,"  \"hull\": { \"mins\": [%.9g,%.9g,%.9g], \"maxs\": [%.9g,%.9g,%.9g] },\n",
        mp.player_mins.x,mp.player_mins.y,mp.player_mins.z, mp.player_maxs.x,mp.player_maxs.y,mp.player_maxs.z);
    fprintf(f,"  \"start\": { \"origin\": [%.9g,%.9g,%.9g], \"velocity\": [%.9g,%.9g,%.9g], \"vangle\": [%.9g,%.9g,%.9g], \"flags\": %d },\n",
        e.origin.x,e.origin.y,e.origin.z, e.velocity.x,e.velocity.y,e.velocity.z, e.v_angle.x,e.v_angle.y,e.v_angle.z, e.flags);
    fprintf(f,"  \"ticks\": [\n");
    for(int t=0;t<s->nticks;t++){
        pinput in = s->in[t];
        g_time += in.dt;
        player_move(&e, &mp, in);
        fprintf(f,"    { \"in\": { \"ang\": [%.9g,%.9g,%.9g], \"move\": [%.9g,%.9g,%.9g], \"jump\": %d, \"crouch\": %d, \"dt\": %.9g },",
            in.angles.x,in.angles.y,in.angles.z, in.move.x,in.move.y,in.move.z, in.jump, in.crouch, in.dt);
        fprintf(f," \"out\": { \"origin\": [%.9g,%.9g,%.9g], \"velocity\": [%.9g,%.9g,%.9g], \"onground\": %d, \"waterlevel\": %d } }%s\n",
            e.origin.x,e.origin.y,e.origin.z, e.velocity.x,e.velocity.y,e.velocity.z,
            (e.flags&FL_ONGROUND)?1:0, e.waterlevel, t+1<s->nticks?",":"");
    }
    fprintf(f,"  ]\n}\n");
}

/* --- world builders --- */
static cworld flat_world(void){
    cworld w; w.nbrushes=0;
    w.brushes[w.nbrushes++] = box_brush(v3(-8192,-8192,-256), v3(8192,8192,0), CONT_SOLID);
    return w;
}
static cworld open_world(void){ cworld w; w.nbrushes=0; return w; }
static cworld stairs_world(void){
    cworld w; w.nbrushes=0;
    w.brushes[w.nbrushes++] = box_brush(v3(-4096,-4096,-256), v3(64,4096,0), CONT_SOLID);
    w.brushes[w.nbrushes++] = box_brush(v3(64,-4096,-256), v3(4096,4096,24), CONT_SOLID);
    return w;
}
static cworld ramp_world(void){
    cworld w; w.nbrushes=0;
    w.brushes[w.nbrushes++] = box_brush(v3(-4096,-4096,-256), v3(0,4096,0), CONT_SOLID);
    cbrush b; b.nplanes=0; b.contents=CONT_SOLID;
    float s30=0.5f, c30=0.8660254f;
    b.planes[b.nplanes++] = (cplane){ v3(-s30,0,c30), 0.0f };
    b.planes[b.nplanes++] = (cplane){ v3(1,0,0), 4096.0f };
    b.planes[b.nplanes++] = (cplane){ v3(-1,0,0), 0.0f };
    b.planes[b.nplanes++] = (cplane){ v3(0,1,0), 4096.0f };
    b.planes[b.nplanes++] = (cplane){ v3(0,-1,0), 4096.0f };
    b.planes[b.nplanes++] = (cplane){ v3(0,0,-1), 256.0f };
    w.brushes[w.nbrushes++] = b;
    return w;
}
static cworld water_world(void){
    cworld w; w.nbrushes=0;
    w.brushes[w.nbrushes++] = box_brush(v3(-4096,-4096,-1024), v3(4096,4096,-256), CONT_SOLID);
    w.brushes[w.nbrushes++] = box_brush(v3(-4096,-4096,-256), v3(4096,4096,256), CONT_WATER);
    return w;
}

static void set_ticks(scenario *s, int n){ s->nticks=n; }

int main(int argc, char **argv){
    const char *outdir = argc>1 ? argv[1] : ".";
    char path[1024];
    /* feet at z=0 -> origin.z = -mins.z = 24, plus the small rest gap the engine leaves between the hull and
       the surface (a trace stops DIST_EPSILON short). Starting exactly on the expanded contact plane would
       read as startsolid; an eighth-unit gap matches a freshly-landed player and lets the slide proceed. */
    const float GROUNDZ = 24.125f;
    const float DT = 1.0f/32.0f;

    { /* 1 */
        scenario s; memset(&s,0,sizeof(s));
        s.name="ground_accel_forward"; s.desc="rest on flat ground, hold +forward to max speed";
        s.world=flat_world(); s.start_origin=v3(0,0,GROUNDZ); s.start_velocity=v3(0,0,0);
        s.start_vangle=v3(0,0,0); s.start_flags=FL_ONGROUND|FL_JUMPRELEASED;
        for(int t=0;t<96;t++){ pinput pi={ v3(0,0,0), v3(400,0,0), 0,0, DT }; s.in[t]=pi; }
        set_ticks(&s,96);
        snprintf(path,sizeof(path),"%s/ground_accel_forward.json",outdir);
        FILE *f=fopen(path,"wb"); emit_json(f,&s); fclose(f);
    }
    { /* 2 */
        scenario s; memset(&s,0,sizeof(s));
        s.name="ground_friction_stop"; s.desc="moving at 320 on flat ground, no input -> friction decel";
        s.world=flat_world(); s.start_origin=v3(0,0,GROUNDZ); s.start_velocity=v3(320,0,0);
        s.start_vangle=v3(0,0,0); s.start_flags=FL_ONGROUND|FL_JUMPRELEASED;
        for(int t=0;t<96;t++){ pinput pi={ v3(0,0,0), v3(0,0,0), 0,0, DT }; s.in[t]=pi; }
        set_ticks(&s,96);
        snprintf(path,sizeof(path),"%s/ground_friction_stop.json",outdir);
        FILE *f=fopen(path,"wb"); emit_json(f,&s); fclose(f);
    }
    { /* 3 */
        scenario s; memset(&s,0,sizeof(s));
        s.name="forward_jump_arc"; s.desc="run forward, jump once, gravity arc with forward air-accel, land";
        s.world=flat_world(); s.start_origin=v3(0,0,GROUNDZ); s.start_velocity=v3(280,0,0);
        s.start_vangle=v3(0,0,0); s.start_flags=FL_ONGROUND|FL_JUMPRELEASED;
        for(int t=0;t<64;t++){ int jump=(t==0); pinput pi={ v3(0,0,0), v3(400,0,0), jump,0, DT }; s.in[t]=pi; }
        set_ticks(&s,64);
        snprintf(path,sizeof(path),"%s/forward_jump_arc.json",outdir);
        FILE *f=fopen(path,"wb"); emit_json(f,&s); fclose(f);
    }
    { /* 4 */
        scenario s; memset(&s,0,sizeof(s));
        s.name="strafe_jump_air"; s.desc="airborne, +x velocity, view turning, strafe-only input -> air speed gain";
        s.world=open_world(); s.start_origin=v3(0,0,512); s.start_velocity=v3(320,0,0);
        s.start_vangle=v3(0,0,0); s.start_flags=FL_JUMPRELEASED;
        for(int t=0;t<80;t++){ float yaw=18.0f+0.30f*t; pinput pi={ v3(0,yaw,0), v3(0,400,0), 0,0, DT }; s.in[t]=pi; }
        set_ticks(&s,80);
        snprintf(path,sizeof(path),"%s/strafe_jump_air.json",outdir);
        FILE *f=fopen(path,"wb"); emit_json(f,&s); fclose(f);
    }
    { /* 5 */
        scenario s; memset(&s,0,sizeof(s));
        s.name="bunnyhop_chain"; s.desc="flat ground, hold jump+forward+strafe, view turning -> repeated hops";
        s.world=flat_world(); s.start_origin=v3(0,0,GROUNDZ); s.start_velocity=v3(300,0,0);
        s.start_vangle=v3(0,0,0); s.start_flags=FL_ONGROUND|FL_JUMPRELEASED;
        for(int t=0;t<160;t++){ float yaw=0.15f*t; pinput pi={ v3(0,yaw,0), v3(360,200,0), 1,0, DT }; s.in[t]=pi; }
        set_ticks(&s,160);
        snprintf(path,sizeof(path),"%s/bunnyhop_chain.json",outdir);
        FILE *f=fopen(path,"wb"); emit_json(f,&s); fclose(f);
    }
    { /* 6 */
        scenario s; memset(&s,0,sizeof(s));
        s.name="air_control_turn"; s.desc="airborne, hold forward, sweep view -> CPM aircontrol curves velocity";
        s.world=open_world(); s.start_origin=v3(0,0,512); s.start_velocity=v3(400,0,0);
        s.start_vangle=v3(0,0,0); s.start_flags=FL_JUMPRELEASED;
        for(int t=0;t<80;t++){ float yaw=0.75f*t; pinput pi={ v3(0,yaw,0), v3(400,0,0), 0,0, DT }; s.in[t]=pi; }
        set_ticks(&s,80);
        snprintf(path,sizeof(path),"%s/air_control_turn.json",outdir);
        FILE *f=fopen(path,"wb"); emit_json(f,&s); fclose(f);
    }
    { /* 7 */
        scenario s; memset(&s,0,sizeof(s));
        s.name="free_fall"; s.desc="open air, no input, pure gravity half-step integration";
        s.world=open_world(); s.start_origin=v3(0,0,1024); s.start_velocity=v3(0,0,0);
        s.start_vangle=v3(0,0,0); s.start_flags=FL_JUMPRELEASED;
        for(int t=0;t<48;t++){ pinput pi={ v3(0,0,0), v3(0,0,0), 0,0, DT }; s.in[t]=pi; }
        set_ticks(&s,48);
        snprintf(path,sizeof(path),"%s/free_fall.json",outdir);
        FILE *f=fopen(path,"wb"); emit_json(f,&s); fclose(f);
    }
    { /* 8 */
        scenario s; memset(&s,0,sizeof(s));
        s.name="ramp_run_up"; s.desc="run in +x onto a 30-degree ramp; slide/clip along the incline";
        s.world=ramp_world(); s.start_origin=v3(-200,0,GROUNDZ); s.start_velocity=v3(360,0,0);
        s.start_vangle=v3(0,0,0); s.start_flags=FL_ONGROUND|FL_JUMPRELEASED;
        for(int t=0;t<80;t++){ pinput pi={ v3(0,0,0), v3(400,0,0), 0,0, DT }; s.in[t]=pi; }
        set_ticks(&s,80);
        snprintf(path,sizeof(path),"%s/ramp_run_up.json",outdir);
        FILE *f=fopen(path,"wb"); emit_json(f,&s); fclose(f);
    }
    { /* 9 */
        scenario s; memset(&s,0,sizeof(s));
        s.name="stair_step_up"; s.desc="walk in +x into a 24u step (stepheight 31) -> step up";
        s.world=stairs_world(); s.start_origin=v3(0,0,GROUNDZ); s.start_velocity=v3(200,0,0);
        s.start_vangle=v3(0,0,0); s.start_flags=FL_ONGROUND|FL_JUMPRELEASED;
        for(int t=0;t<64;t++){ pinput pi={ v3(0,0,0), v3(300,0,0), 0,0, DT }; s.in[t]=pi; }
        set_ticks(&s,64);
        snprintf(path,sizeof(path),"%s/stair_step_up.json",outdir);
        FILE *f=fopen(path,"wb"); emit_json(f,&s); fclose(f);
    }
    { /* 10 */
        scenario s; memset(&s,0,sizeof(s));
        s.name="swim_forward"; s.desc="submerged in water, hold forward -> water friction + water accel";
        s.world=water_world(); s.start_origin=v3(0,0,0); s.start_velocity=v3(0,0,0);
        s.start_vangle=v3(0,0,0); s.start_flags=FL_JUMPRELEASED;
        for(int t=0;t<64;t++){ pinput pi={ v3(0,0,0), v3(400,0,0), 0,0, DT }; s.in[t]=pi; }
        set_ticks(&s,64);
        snprintf(path,sizeof(path),"%s/swim_forward.json",outdir);
        FILE *f=fopen(path,"wb"); emit_json(f,&s); fclose(f);
    }

    printf("wrote 10 golden scenarios to %s\n", outdir);
    return 0;
}
