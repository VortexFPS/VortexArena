// Port of two controller-driven brush movers:
//   qcsrc/common/mapobjects/func/fourier.qc        -> func_fourier        (sum-of-sines mover)
//   qcsrc/common/mapobjects/func/vectormamamam.qc  -> func_vectormamamam  (4-reference projected mover)
//
// Both are MOVETYPE_PUSH brushes whose velocity is re-driven every controller tick toward a target point so
// they arrive there next frame (the same "*10 so it arrives in 0.1 sec" technique func_bobbing uses):
//  - func_fourier sums a netname-list of <freqmul phase x y z> sine quintuples about the yaw axis, scaled by
//    .height, added onto .destvec (the spawn origin). Controller think every 0.1s.
//  - func_vectormamamam tracks up to four external reference entities (.wp00-03, resolved from .target..target4)
//    and builds a point by projecting each reference's predicted position onto (or off of) a per-reference
//    normal, weighted by a per-reference factor. Controller think every VECTORMAMAMAM_TIMESTEP (0.1s).
//
// Port notes:
//  * QC func_fourier stores 360/speed in .cnt (a FLOAT use); the port uses Entity.MoverCnt (the float .cnt the
//    bobbing/pendulum ports already use), NOT the int Cnt.
//  * tokenize_console + stof + makevectors are ported as small local helpers (WS-split, invariant float parse,
//    QMath.Forward) — the netname is a plain space-separated number list, so a whitespace split is faithful.
//  * The looping ambient .noise (soundto MSG_INIT / per-player MSG_ONE) is server→client networking; the port
//    plays it through the sound facade where one exists and skips the per-player MSG_ONE resend (no live client
//    roster at this layer — same reduction CompatRemaps notes). The MOVER behavior is fully faithful.
//  * func_vectormamamam's reference lookups run at INITPRIO_FINDTARGET, drained by RunPostSpawn; its controller
//    is spawned there (QC func_vectormamamam_findtarget). func_fourier's controller is spawned inline at spawn
//    (QC spawnfunc, controller.nextthink = time+1) exactly as Base does.

using System.Globalization;
using System.Numerics;
using XonoticGodot.Common.Framework;
using XonoticGodot.Common.Math;
using XonoticGodot.Common.Services;

namespace XonoticGodot.Common.Framework
{
    public partial class Entity
    {
        // ---- func_vectormamamam reference entities (vectormamamam.qc:4 — reusing havocbot .wp00-03) ----
        public Entity? Wp00, Wp01, Wp02, Wp03;

        // ---- func_vectormamamam per-reference weights + projection normals (vectormamamam.qc:6-7) ----
        public float TargetFactor, Target2Factor, Target3Factor, Target4Factor;
        public Vector3 TargetNormal, Target2Normal, Target3Normal, Target4Normal;
    }
}

namespace XonoticGodot.Common.Gameplay
{
    using XonoticGodot.Common.Framework;

    /// <summary><c>func_fourier</c> + <c>func_vectormamamam</c> — controller-driven brush movers. Registered by <see cref="MapObjectsRegistry"/>.</summary>
    public static class AdvancedMovers
    {
        // ---- vectormamamam.qh ----
        public const int ProjectOnTargetNormal = 1 << 0;  // PROJECT_ON_TARGETNORMAL
        public const int ProjectOnTarget2Normal = 1 << 1; // PROJECT_ON_TARGET2NORMAL
        public const int ProjectOnTarget3Normal = 1 << 2; // PROJECT_ON_TARGET3NORMAL
        public const int ProjectOnTarget4Normal = 1 << 3; // PROJECT_ON_TARGET4NORMAL
        public const float VectormamamamTimestep = 0.1f;  // VECTORMAMAMAM_TIMESTEP

        /// <summary>DP <c>EF_LOWPRECISION = 4194304</c> (dpextensions.qc:274) — a bandwidth hint (lower-precision
        /// origin networking). No gameplay effect; set on the effects bitmap to mirror Base exactly. The shared
        /// EffectFlags table (owned elsewhere) doesn't carry it, so it's defined here.</summary>
        public const int EfLowPrecision = 4194304;

        // The INITPRIO_FINDTARGET deferred-resolve queue for func_vectormamamam (drained by RunPostSpawn).
        private static readonly List<Entity> _pendingVmInit = new();

        // ===================================================================
        //  func_fourier
        // ===================================================================

        /// <summary><c>spawnfunc(func_fourier)</c> (fourier.qc:43-88).</summary>
        public static void FourierSetup(Entity this_)
        {
            this_.ClassName = "func_fourier";

            // QC fourier.qc:46-49: precache + looping soundto(MSG_INIT, this, CH_TRIGGER_SINGLE, .noise, VOL_BASE,
            // ATTEN_IDLE, 0) — a persistent loop on the mover, replayed to every (incl. late-joining) client via
            // the entity's sound state (the headless analogue of the per-player MSG_INIT resend).
            MapMover.LoopAmbient(this_, this_.Noise);

            if (this_.Speed == 0f) this_.Speed = 4f;                        // QC: if (!this.speed) this.speed = 4;
            if (this_.Height == 0f) this_.Height = 32f;                     // QC: if (!this.height) this.height = 32;
            this_.DestVec = this_.Origin;                                   // QC: this.destvec = this.origin;
            this_.MoverCnt = 360f / this_.Speed;                           // QC: this.cnt = 360 / this.speed;

            this_.Blocked = MapMover.GenericPlatBlocked;                    // QC: setblocked(generic_plat_blocked);
            if (this_.Dmg != 0f && string.IsNullOrEmpty(this_.Message))
                this_.Message = " was squished";
            if (this_.Dmg != 0f && string.IsNullOrEmpty(this_.Message2))
                this_.Message2 = "was squished by";
            if (this_.Dmg != 0f && this_.CrushInterval == 0f)
                this_.CrushInterval = 0.25f;
            this_.CrushNextTime = MapMover.Now();                           // QC: this.dmgtime2 = time;

            if (string.IsNullOrEmpty(this_.NetName))                        // QC: if(this.netname == "") this.netname = "1 0 0 0 1";
                this_.NetName = "1 0 0 0 1";

            if (!MapMover.InitMovingBrushTrigger(this_))                    // QC: if (!InitMovingBrushTrigger(this)) return;
                return;

            this_.Active = MapMover.ActiveActive;                           // QC: this.active = ACTIVE_ACTIVE;
            MapMover.IndexRegister(this_);

            // QC: spawn the controller; park the mover for PushMove (SUB_NullThink).
            if (Api.Services is not null)
            {
                Entity controller = Api.Entities.Spawn();
                controller.ClassName = "func_fourier_controller";
                controller.Owner = this_;
                controller.NextThink = MapMover.Now() + 1f;
                controller.Think = FourierControllerThink;
            }
            this_.NextThink = this_.LTime + 999999999f;                     // QC: this.nextthink = this.ltime + 999999999;
            this_.Think = MapMover.NullThink;                              // QC: setthink(this, SUB_NullThink);

            this_.Effects |= EfLowPrecision;                      // QC: this.effects |= EF_LOWPRECISION;
        }

        /// <summary>Port of <c>func_fourier_controller_think</c> (fourier.qc:14-41).</summary>
        private static void FourierControllerThink(Entity this_)
        {
            Entity owner = this_.Owner!;
            this_.NextThink = MapMover.Now() + 0.1f;

            if (owner.Active != MapMover.ActiveActive)                      // QC: if(owner.active != ACTIVE_ACTIVE) { vel=0; return; }
            {
                owner.Velocity = Vector3.Zero;
                return;
            }

            // QC: n = floor(tokenize_console(owner.netname) / 5);
            string[] argv = Tokenize(owner.NetName);
            int n = argv.Length / 5;
            // QC: t = this.nextthink * owner.cnt + owner.phase * 360;
            float t = this_.NextThink * owner.MoverCnt + owner.Phase * 360f;

            Vector3 v = owner.DestVec;                                      // QC: v = owner.destvec;

            for (int i = 0; i < n; ++i)
            {
                // QC: makevectors((t * stof(argv(i*5)) + stof(argv(i*5+1)) * 360) * '0 1 0');
                float angDeg = t * Stof(argv[i * 5]) + Stof(argv[i * 5 + 1]) * 360f;
                Vector3 fwd = QMath.Forward(new Vector3(0f, angDeg, 0f));
                // QC: v += ('1 0 0'*x + '0 1 0'*y + '0 0 1'*z) * owner.height * v_forward_y;
                Vector3 amp = new(Stof(argv[i * 5 + 2]), Stof(argv[i * 5 + 3]), Stof(argv[i * 5 + 4]));
                v += amp * (owner.Height * fwd.Y);
            }

            // QC: if(owner.classname == "func_fourier") owner.velocity = (v - owner.origin) * 10;
            if (owner.ClassName == "func_fourier")
                owner.Velocity = (v - owner.Origin) * 10f;
        }

        // ===================================================================
        //  func_vectormamamam
        // ===================================================================

        /// <summary><c>spawnfunc(func_vectormamamam)</c> (vectormamamam.qc:132-190).</summary>
        public static void VectormamamamSetup(Entity this_)
        {
            this_.ClassName = "func_vectormamamam";

            // QC: precache_sound(noise) (the looping ambient is networked; played via the facade below).

            if (this_.TargetFactor == 0f) this_.TargetFactor = 1f;         // QC: if(!targetfactor) targetfactor = 1;
            if (this_.Target2Factor == 0f) this_.Target2Factor = 1f;
            if (this_.Target3Factor == 0f) this_.Target3Factor = 1f;
            if (this_.Target4Factor == 0f) this_.Target4Factor = 1f;

            if (this_.TargetNormal != Vector3.Zero) this_.TargetNormal = QMath.Normalize(this_.TargetNormal);
            if (this_.Target2Normal != Vector3.Zero) this_.Target2Normal = QMath.Normalize(this_.Target2Normal);
            if (this_.Target3Normal != Vector3.Zero) this_.Target3Normal = QMath.Normalize(this_.Target3Normal);
            if (this_.Target4Normal != Vector3.Zero) this_.Target4Normal = QMath.Normalize(this_.Target4Normal);

            this_.Blocked = MapMover.GenericPlatBlocked;                    // QC: setblocked(generic_plat_blocked);
            if (this_.Dmg != 0f && string.IsNullOrEmpty(this_.Message))
                this_.Message = " was squished";
            if (this_.Dmg != 0f && string.IsNullOrEmpty(this_.Message2))
                this_.Message2 = "was squished by";
            if (this_.Dmg != 0f && this_.CrushInterval == 0f)
                this_.CrushInterval = 0.25f;
            this_.CrushNextTime = MapMover.Now();                           // QC: this.dmgtime2 = time;

            if (!MapMover.InitMovingBrushTrigger(this_))                    // QC: if (!InitMovingBrushTrigger(this)) return;
                return;

            this_.NextThink = this_.LTime + 999999999f;                     // QC: this.nextthink = this.ltime + 999999999;
            this_.Think = MapMover.NullThink;                              // QC: setthink(this, SUB_NullThink);

            this_.Effects |= EfLowPrecision;                      // QC: this.effects |= EF_LOWPRECISION;

            this_.SetActive = VectormamamamSetActive;                       // QC: this.setactive = func_vectormamamam_setactive;
            VectormamamamSetActive(this_, MapMover.ActiveActive);           // QC: this.setactive(this, ACTIVE_ACTIVE);

            // QC: IL_PUSH(g_initforplayer, this); this.init_for_player = func_vectormamamam_init_for_player — the
            // per-player MSG_ONE ambient resend for late joiners. The port reproduces the net effect without a
            // client roster: VectormamamamSetActive starts a PERSISTENT loop (MapMover.LoopAmbient), which the
            // facade keeps as part of the entity's sound state and replays to any client that connects later.

            MapMover.IndexRegister(this_);

            // QC: InitializeEntity(this, func_vectormamamam_findtarget, INITPRIO_FINDTARGET);
            if (!_pendingVmInit.Contains(this_))
                _pendingVmInit.Add(this_);
        }

        /// <summary>
        /// Resolve every queued func_vectormamamam's reference entities + spawn its controller — the headless
        /// analogue of QC's INITPRIO_FINDTARGET pass (run by <see cref="MapObjectsRegistry.RunPostSpawn"/>).
        /// </summary>
        public static void RunDeferredInit()
        {
            if (_pendingVmInit.Count == 0)
                return;
            Entity[] batch = _pendingVmInit.ToArray();
            _pendingVmInit.Clear();
            foreach (Entity e in batch)
                if (!e.IsFreed)
                    VectormamamamFindTarget(e);
        }

        /// <summary>Port of <c>func_vectormamamam_findtarget</c> (vectormamamam.qc:73-96).</summary>
        private static void VectormamamamFindTarget(Entity this_)
        {
            if (!string.IsNullOrEmpty(this_.Target))  this_.Wp00 = MapMover.FindFirstByTargetName(this_.Target);
            if (!string.IsNullOrEmpty(this_.Target2)) this_.Wp01 = MapMover.FindFirstByTargetName(this_.Target2);
            if (!string.IsNullOrEmpty(this_.Target3)) this_.Wp02 = MapMover.FindFirstByTargetName(this_.Target3);
            if (!string.IsNullOrEmpty(this_.Target4)) this_.Wp03 = MapMover.FindFirstByTargetName(this_.Target4);

            // QC: if(!wp00 && !wp01 && !wp02 && !wp03) objerror(...);
            if (this_.Wp00 is null && this_.Wp01 is null && this_.Wp02 is null && this_.Wp03 is null)
                return;

            // QC: this.destvec = this.origin - func_vectormamamam_origin(this, 0);
            this_.DestVec = this_.Origin - VectormamamamOrigin(this_, 0f);

            if (Api.Services is not null)
            {
                Entity controller = Api.Entities.Spawn();
                controller.ClassName = "func_vectormamamam_controller";
                controller.Owner = this_;
                controller.NextThink = MapMover.Now() + 1f;
                controller.Think = VectormamamamControllerThink;
            }
        }

        /// <summary>Port of <c>func_vectormamamam_origin</c> (vectormamamam.qc:9-57): sum each reference's projected position.</summary>
        private static Vector3 VectormamamamOrigin(Entity o, float timestep)
        {
            int myflags = o.SpawnFlags;
            Vector3 v = Vector3.Zero;

            Accumulate(ref v, o.Wp00, o.TargetNormal, o.TargetFactor, (myflags & ProjectOnTargetNormal) != 0, timestep);
            Accumulate(ref v, o.Wp01, o.Target2Normal, o.Target2Factor, (myflags & ProjectOnTarget2Normal) != 0, timestep);
            Accumulate(ref v, o.Wp02, o.Target3Normal, o.Target3Factor, (myflags & ProjectOnTarget3Normal) != 0, timestep);
            Accumulate(ref v, o.Wp03, o.Target4Normal, o.Target4Factor, (myflags & ProjectOnTarget4Normal) != 0, timestep);

            return v;
        }

        /// <summary>
        /// One reference's contribution: predict p = e.origin + timestep*e.velocity, then add either the
        /// projection ONTO the normal (PROJECT_ON_*) or the projection OFF the normal (the perpendicular
        /// remainder), each scaled by the factor. Exactly QC's per-reference branch (vectormamamam.qc:17-24).
        /// </summary>
        private static void Accumulate(ref Vector3 v, Entity? e, Vector3 normal, float factor, bool projectOn, float timestep)
        {
            if (e is null)
                return;
            Vector3 p = e.Origin + timestep * e.Velocity;
            float dot = QMath.Dot(p, normal); // QC: p * o.targetnormal (dot product)
            if (projectOn)
                v += (dot * normal) * factor;                  // QC: (p * n) * n * factor
            else
                v += (p - dot * normal) * factor;              // QC: (p - (p * n) * n) * factor
        }

        /// <summary>Port of <c>func_vectormamamam_controller_think</c> (vectormamamam.qc:59-71).</summary>
        private static void VectormamamamControllerThink(Entity this_)
        {
            this_.NextThink = MapMover.Now() + VectormamamamTimestep;

            Entity owner = this_.Owner!;
            if (owner.Active != MapMover.ActiveActive)                      // QC: if(owner.active != ACTIVE_ACTIVE) { vel=0; return; }
            {
                owner.Velocity = Vector3.Zero;
                return;
            }

            // QC: owner.velocity = (owner.destvec + func_vectormamamam_origin(owner, TIMESTEP) - owner.origin) * 10;
            if (owner.ClassName == "func_vectormamamam")
                owner.Velocity = (owner.DestVec + VectormamamamOrigin(owner, VectormamamamTimestep) - owner.Origin) * 10f;
        }

        /// <summary>Port of <c>func_vectormamamam_setactive</c> (vectormamamam.qc:98-121): toggle + start/stop the ambient.</summary>
        private static void VectormamamamSetActive(Entity this_, int astate)
        {
            if (astate == MapMover.ActiveToggle)
                this_.Active = this_.Active == MapMover.ActiveActive ? MapMover.ActiveNot : MapMover.ActiveActive;
            else
                this_.Active = astate;

            if (this_.Active == MapMover.ActiveNot)
            {
                MapMover.StopAmbient(this_);                               // QC: stopsound(this, CH_TRIGGER_SINGLE);
            }
            else
            {
                // QC: _sound(this, CH_TRIGGER_SINGLE, noise, VOL_BASE, ATTEN_IDLE) — a persistent loop, so a
                // late-joining player still hears the ambient (the headless analogue of QC's per-player
                // func_vectormamamam_init_for_player MSG_ONE resend; no separate client roster at this layer).
                MapMover.LoopAmbient(this_, this_.Noise);
            }
        }

        // ===================================================================
        //  small QC builtins (tokenize_console / stof)
        // ===================================================================

        /// <summary>QC <c>tokenize_console</c> reduced to a whitespace split (the netname is a plain number list).</summary>
        private static string[] Tokenize(string s)
            => string.IsNullOrEmpty(s) ? System.Array.Empty<string>()
             : s.Split((char[]?)null, System.StringSplitOptions.RemoveEmptyEntries);

        /// <summary>QC <c>stof</c>: parse a token as a float (0 on failure), invariant culture.</summary>
        private static float Stof(string s)
            => float.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out float v) ? v : 0f;
    }
}
