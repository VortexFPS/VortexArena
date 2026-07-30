// Port of the remaining flat QC entity fields the map-entity "content tail" reads off the edict
// (qcsrc/common/mapobjects/misc/laser.qc, models.qc, func/pointparticles.qc, func/rainsnow.qc,
// target/music.qc), plus the one generic key-parse helper the spawn chokepoints call.
//
// In DarkPlaces, ED_ParseEdict copies EVERY "key" "value" pair of a map entity onto the matching QC
// field generically. The port's GameWorld.ApplyDictFields (and GameDemo.ApplyMapFields) instead promote a
// fixed key set — so each new field family needs (a) the Entity partial fields (ADR-0007: new file, no
// Entity.cs edits) and (b) a parse hook. Apply() below is that hook: the GameWorld/GameDemo chokepoints
// call it with the raw field dict, keeping the chokepoint itself one line.

using System.Globalization;
using System.Numerics;

namespace XonoticGodot.Common.Framework
{
    public partial class Entity
    {
        // ---- effect-name / emitter keys (laser.qc / pointparticles.qc) ----
        /// <summary>QC <c>.mdl</c> — an effectinfo effect NAME (laser end effect, pointparticles emitter).</summary>
        public string Mdl = "";
        /// <summary>QC <c>.impulse</c> as a map key — func_pointparticles emission density (per second; negative = relative per-64³).</summary>
        public float Impulse;
        /// <summary>QC <c>.waterlevel</c> reused as the func_pointparticles velocity JITTER magnitude.</summary>
        public float ParticleJitter;
        /// <summary>QC <c>.count</c> as the FLOAT map key (pointparticles per-emission multiplier, rain/snow density).
        /// Distinct from the int <see cref="Count"/> the monster_spawner path promotes.</summary>
        public float ParticleCount;

        // ---- laser keys (laser.qc) ----
        /// <summary>QC <c>.beam_color</c> — misc_laser beam tint.</summary>
        public Vector3 BeamColor;
        /// <summary>QC <c>.colormod</c> map key — the LEGACY alias of beam_color (named *Key to avoid clashing with
        /// any render-side colormod promotion).</summary>
        public Vector3 ColorModKey;
        /// <summary>The raw <c>alpha</c> map key (0 = key absent). QC semantics branch on "alpha set/0"
        /// (laser.qc:244-248), and <see cref="Alpha"/> defaults to 1 and drives render fades — so the raw key is
        /// recorded separately and each spawnfunc decides what to do with it.</summary>
        public float AlphaKey;
        /// <summary>Port-side resolution of the QC laser <c>.cnt</c> end-effect: the effectinfo NAME to play where
        /// the beam hits ("" = no effect, QC cnt -1). The port resolves effects by name, not number.</summary>
        public string LaserEndEffect = "";
        /// <summary>The misc_laser DETECTOR edge latch (QC reuses <c>.count</c>, laser.qc:95-109 — kept separate
        /// here because <c>count</c> is a plumbed map key on this port's Entity).</summary>
        public bool LaserHitLatch;
        /// <summary>Port: the INITPRIO_FINDTARGET enemy lookup ran (done lazily on the first laser think).</summary>
        public bool LaserTargetSearched;

        // ---- model-prop keys (models.qc / laser.qc) ----
        /// <summary>QC <c>.scale</c> — model/render scale (misc_gamemodel props; laser beam radius). Named
        /// ScaleFactor to avoid colliding with other partials.</summary>
        public float ScaleFactor;
        /// <summary>QC <c>.modelscale</c> — fallback scale (models.qc) / laser dlight radius factor.</summary>
        public float ModelScale;
        /// <summary>The raw <c>solid</c> map key (0 = unset; &lt;0 forces SOLID_NOT — models.qc:178-179).</summary>
        public float SolidOverride;
        /// <summary>RENDER_COLORMAPPED (QC BIT(10)) — the marker bit on <see cref="ColorMapOverride"/> saying the
        /// value is an authoritative colormap the renderer should apply. NOTE: numerically identical to QC's
        /// <c>1024 +</c> packed-colormap bias (colormap ≥ 1024 = colormapped) — that overlap is QC's own encoding.
        /// The ONE shared definition for every producer/consumer (WeaponThrowing, MonsterAI, MapModels, the
        /// snapshot encode/decode, the client item paint) — do not re-declare locally.</summary>
        public const int RenderColormapped = 1 << 10;

        /// <summary>QC <c>.colormap</c> as set by g_model_setcolormaptoactivator (models.qc:17-29). Render-side
        /// consumption is a follow-up; the field keeps the QC math observable.</summary>
        public int ColorMapOverride;

        /// <summary>The DP <c>MF_ROTATE</c> model flag (csqcmodel_hooks.qc:617-623). The ONLY edict that sets it
        /// is the pickup key (keys.qc:117). DP's predraw spins the model in yaw by
        /// <c>'0 100 0' * fmod(time, 3.6)</c> (a steady 100°/s, wrapping every 3.6 s = 360°). The port has no
        /// networked modelflags channel; on the listen-server/demo path the shared edict carries this bool and
        /// EntityNode applies the same yaw spin. (Render-only; the server entity's .angles is unchanged.)</summary>
        public bool ModelSpinRotate;

        // ---- distance-fade keys (models.qc ENT_CLIENT_WALL / Ent_Wall_PreDraw) ----
        public float FadeStartDist;      // QC .fade_start
        public float FadeEndDist;        // QC .fade_end
        public float AlphaMax;           // QC .alpha_max — PERCENT (the /100 in Ent_Wall_PreDraw is load-bearing)
        public float AlphaMin;           // QC .alpha_min — PERCENT
        public float FadeVerticalOffset; // QC .fade_vertical_offset

        // ---- antiwall relay (models.qc g_clientmodel_use) ----
        /// <summary>QC <c>.antiwall_flag</c> — the func_clientwall solid-toggle command relayed from the firing
        /// trigger: 1 = deactivate (SOLID_NOT + hidden), 2 = activate (restore default_solid + shown), 0 = nothing.
        /// A trigger that toggles a clientwall sets this on itself; g_clientmodel_use copies it onto the wall.</summary>
        public int AntiwallFlag;
        /// <summary>QC <c>.inactive</c> — a func_clientwall/func_clientillusionary deactivated by the antiwall
        /// relay (renders alpha 0 in Ent_Wall_PreDraw and, if SOLID, is also made non-solid).</summary>
        public bool Inactive;
        /// <summary>QC <c>.default_solid</c> — the wall's original solidity (set at g_clientmodel_init), restored by
        /// the antiwall relay's "activate" (flag 2) branch.</summary>
        public Solid DefaultSolid;

        // ---- weather (rainsnow.qc) ----
        /// <summary>QC <c>.dest</c> — the rain/snow fall velocity (moved off <c>.velocity</c> at spawn).</summary>
        public Vector3 Dest;

        // ---- trigger_multiple pressed-keys gate (multi.qc:135-137) ----
        /// <summary>QC <c>.pressedkeys</c> as a MAP key on a trigger_multiple: a bitmask of the movement keys
        /// (KEY_FORWARD/BACKWARD/LEFT/RIGHT/JUMP/CROUCH/ATCK/ATCK2 — the PressedKeyBits enum) the player must
        /// be holding for the trigger to fire. Distinct from the per-frame <see cref="Entity.PressedKeys"/> the
        /// input/dodging layer rewrites on a player edict each frame — this one is parsed once from the map and
        /// never touched, so it can't collide with the player's live key state.</summary>
        public int TriggerPressedKeys; // QC trigger-side .pressedkeys (multi.qc)

        // ---- push interaction (triggers.qc isPushable) ----
        /// <summary>QC <c>.pushable</c> — when set, the entity is moved by jumppads/conveyors/impulse-fields even
        /// if it would otherwise be excluded (notably the spiderbot vehicle: vr_setup sets <c>pushable = true</c>
        /// so it can ride jumppads, despite IS_VEHICLE normally returning false). Checked FIRST by
        /// <c>isPushable</c> (triggers.qc:5), before the IS_VEHICLE/creature/loot tests.</summary>
        public bool PushableFlag;

        // ---- music keys (target/music.qc) ----
        public float MusicLifetime;      // QC .lifetime — seconds a triggered target_music outranks the default (0 = becomes the default)
        public float MusicFadeIn;        // QC .fade_time — seconds to ramp the track IN
        public float MusicFadeOut;       // QC .fade_rate — seconds to ramp the track OUT
        /// <summary>Port: sim time target_music_use last fired (-1 = never). The client player evaluates the
        /// lifetime window against this (QC stamps <c>e.lifetime = time + tim</c> client-side on receive).</summary>
        public float MusicActivationTime = -1f;
    }
}

namespace XonoticGodot.Common.Gameplay
{
    using XonoticGodot.Common.Framework;

    /// <summary>
    /// Parses the T48 "content tail" map keys onto the edict — the shared helper both spawn paths call
    /// (GameWorld.ApplyDictFields and GameDemo.ApplyMapFields), so the per-host chokepoint stays one line.
    /// Keys already promoted by GameWorld (dmg/volume/atten/cnt/count/noise + the single-float <c>angle</c>
    /// anglehack) are re-parsed here too: idempotent on the server path, and it COMPLETES the demo path,
    /// whose slimmer ApplyMapFields never carried them.
    /// </summary>
    public static class MapObjectFieldsExtra
    {
        public static void Apply(Entity e, System.Collections.Generic.IReadOnlyDictionary<string, string> fields)
        {
            // --- DP anglehack (PRVM_ED_ParseEdict): single-float `angle "Y"` => angles '0 Y 0'. No-op when the
            //     host already applied it (GameWorld); fixes the GameDemo path, which never had it. ---
            if (e.Angles == Vector3.Zero && TryF(fields, "angle", out float yaw))
                e.Angles = new Vector3(0f, yaw, 0f);

            // --- effect / emitter keys ---
            if (fields.TryGetValue("mdl", out string? mdl)) e.Mdl = mdl;
            if (TryF(fields, "impulse", out float imp)) e.Impulse = imp;
            if (TryF(fields, "waterlevel", out float wl)) e.ParticleJitter = wl;
            if (TryF(fields, "count", out float cntF))
            {
                e.ParticleCount = cntF;
                e.Count = (int)cntF; // keep the int promotion in lockstep for the demo path
            }
            if (TryF(fields, "cnt", out float cv)) e.Cnt = (int)cv;

            // --- motion keys ---
            if (TryVec(fields, "velocity", out Vector3 vel)) e.Velocity = vel;
            if (TryVec(fields, "movedir", out Vector3 md)) e.MoveDir = md;
            // trigger_multiple key-gate (multi.qc:135): the player must hold one of these movement keys.
            if (TryF(fields, "pressedkeys", out float pk)) e.TriggerPressedKeys = (int)pk;
            if (TryVec(fields, "mins", out Vector3 mins)) { e.Mins = mins; e.Size = e.Maxs - e.Mins; }
            if (TryVec(fields, "maxs", out Vector3 maxs)) { e.Maxs = maxs; e.Size = e.Maxs - e.Mins; }

            // --- laser keys ---
            if (TryVec(fields, "beam_color", out Vector3 bc)) e.BeamColor = bc;
            if (TryVec(fields, "colormod", out Vector3 cm)) e.ColorModKey = cm;
            if (TryF(fields, "alpha", out float al)) e.AlphaKey = al;

            // --- model-prop keys ---
            if (TryF(fields, "scale", out float sc)) e.ScaleFactor = sc;
            if (TryF(fields, "modelscale", out float msc)) e.ModelScale = msc;
            if (TryF(fields, "solid", out float sol)) e.SolidOverride = sol;
            if (TryF(fields, "fade_start", out float fs)) e.FadeStartDist = fs;
            if (TryF(fields, "fade_end", out float fe)) e.FadeEndDist = fe;
            if (TryF(fields, "alpha_max", out float amx)) e.AlphaMax = amx;
            if (TryF(fields, "alpha_min", out float amn)) e.AlphaMin = amn;
            if (TryF(fields, "fade_vertical_offset", out float fvo)) e.FadeVerticalOffset = fvo;
            // .antiwall_flag rides the TRIGGER that toggles a func_clientwall (1=deactivate, 2=activate);
            // g_clientmodel_use reads it off the activator. (models.qc:42)
            if (TryF(fields, "antiwall_flag", out float awf)) e.AntiwallFlag = (int)awf;

            // --- music keys ---
            if (TryF(fields, "lifetime", out float lt)) e.MusicLifetime = lt;
            if (TryF(fields, "fade_time", out float ft)) e.MusicFadeIn = ft;
            if (TryF(fields, "fade_rate", out float fr)) e.MusicFadeOut = fr;

            // --- target_changelevel keys (QC .chmap / .gametype, changelevel.qc:3) ---
            //     `chmap` = the map to switch to ("" = end the match); `gametype` = the next map's gametype.
            //     Without these the named-chmap + gametype-switch branches of target_changelevel_use are
            //     unreachable (the field stays "" so only the end-match NextLevel branch ever fired).
            if (fields.TryGetValue("chmap", out string? chmap)) e.ChMap = chmap;
            if (fields.TryGetValue("gametype", out string? chgt)) e.ChLevelGameType = chgt;

            // --- keys GameWorld promotes but GameDemo's slimmer twin never did (demo-path completion) ---
            if (TryF(fields, "dmg", out float dmg)) e.Dmg = dmg;
            if (TryF(fields, "volume", out float vol)) e.Volume = vol;
            if (TryF(fields, "atten", out float att)) e.Atten = att;
            if (fields.TryGetValue("noise", out string? ns)) e.Noise = ns;

            // --- Wave-1 mover soundpack + ease keys (MapObjectsCommon: ApplyDoorSounds/ApplyPlatSounds read
            //     .sounds; .sound1/.sound2 are the legacy per-mover move/stop sound overrides; .platmovetype is
            //     parsed by MapMover.SetPlatMoveType at the path_corner/train/plat spawnfuncs). Promoted here so
            //     the soundpack helpers + set_platmovetype have their raw map input. ---
            if (TryF(fields, "sounds", out float snds)) e.Sounds = (int)snds;
            if (fields.TryGetValue("sound1", out string? s1)) e.Sound1 = s1;
            if (fields.TryGetValue("sound2", out string? s2)) e.Sound2 = s2;
            if (fields.TryGetValue("platmovetype", out string? pmt)) e.Platmovetype = pmt;

            // --- bidirectional rotating-door reverse marker (QC .trigger_reverse on the firing trigger) ---
            if (TryF(fields, "trigger_reverse", out float trev)) e.TriggerReverse = (int)trev;

            // --- T59 long-tail map-object keys (the 7 rare entities; not promoted by GameWorld's fixed key set) ---
            // dynlight (light_lev/color/dtagname/style), the generic .target2-4/.delay/.message2/.phase movers read,
            // misc_follow (.jointtype), func_fourier/voicescript (.netname), func_fourier crush interval (.dmgtime),
            // and func_vectormamamam's per-reference factors + projection normals (.target[N]factor/.target[N]normal).
            if (TryF(fields, "light_lev", out float ll)) e.LightLev = ll;
            if (TryVec(fields, "color", out Vector3 col)) e.LightColor = col;
            if (fields.TryGetValue("dtagname", out string? dtn)) e.DTagName = dtn;
            if (TryF(fields, "style", out float st)) e.LightStyle = (int)st;

            if (fields.TryGetValue("netname", out string? nn)) e.NetName = nn;
            if (TryF(fields, "phase", out float ph)) e.Phase = ph;
            if (fields.TryGetValue("target2", out string? t2)) e.Target2 = t2;
            if (fields.TryGetValue("target3", out string? t3)) e.Target3 = t3;
            if (fields.TryGetValue("target4", out string? t4)) e.Target4 = t4;
            if (fields.TryGetValue("message2", out string? m2)) e.Message2 = m2;
            if (TryF(fields, "delay", out float dl)) e.Delay = dl;
            if (TryF(fields, "jointtype", out float jt)) e.JointType = jt;
            if (TryF(fields, "dmgtime", out float dt)) e.CrushInterval = dt;

            if (TryF(fields, "targetfactor", out float tf0)) e.TargetFactor = tf0;
            if (TryF(fields, "target2factor", out float tf1)) e.Target2Factor = tf1;
            if (TryF(fields, "target3factor", out float tf2)) e.Target3Factor = tf2;
            if (TryF(fields, "target4factor", out float tf3)) e.Target4Factor = tf3;
            if (TryVec(fields, "targetnormal", out Vector3 tn0)) e.TargetNormal = tn0;
            if (TryVec(fields, "target2normal", out Vector3 tn1)) e.Target2Normal = tn1;
            if (TryVec(fields, "target3normal", out Vector3 tn2)) e.Target3Normal = tn2;
            if (TryVec(fields, "target4normal", out Vector3 tn3)) e.Target4Normal = tn3;
        }

        private static bool TryF(System.Collections.Generic.IReadOnlyDictionary<string, string> f, string key, out float v)
        {
            v = 0f;
            return f.TryGetValue(key, out string? s)
                && float.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out v);
        }

        private static bool TryVec(System.Collections.Generic.IReadOnlyDictionary<string, string> f, string key, out Vector3 v)
        {
            v = Vector3.Zero;
            if (!f.TryGetValue(key, out string? s) || string.IsNullOrWhiteSpace(s))
                return false;
            string[] p = s.Split((char[]?)null, System.StringSplitOptions.RemoveEmptyEntries);
            if (p.Length < 3)
                return false;
            bool ok = float.TryParse(p[0], NumberStyles.Float, CultureInfo.InvariantCulture, out float x);
            ok &= float.TryParse(p[1], NumberStyles.Float, CultureInfo.InvariantCulture, out float y);
            ok &= float.TryParse(p[2], NumberStyles.Float, CultureInfo.InvariantCulture, out float z);
            if (!ok)
                return false;
            v = new Vector3(x, y, z);
            return true;
        }
    }
}
