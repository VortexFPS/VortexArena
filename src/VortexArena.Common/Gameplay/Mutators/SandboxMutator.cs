using System;
using System.Collections.Generic;
using System.Globalization;
using System.Numerics;
using XonoticGodot.Common.Framework;
using XonoticGodot.Common.Math;
using XonoticGodot.Common.Services;

namespace XonoticGodot.Common.Gameplay;

/// <summary>
/// The Sandbox mutator — port of <c>common/mutators/mutator/sandbox/sv_sandbox.qc</c>. With
/// <c>g_sandbox 1</c> the server lets players freely spawn arbitrary model "objects" in front of
/// themselves and then edit, recolor, rescale, attach, duplicate, claim, drag and persist them — a
/// creative build mode (also the engine behind <c>g_monsters_edit</c>'s shared drag).
///
/// <para>What is ported here, faithfully and self-contained: every cvar default; the per-object property
/// model (<see cref="SandboxObject"/>); the object registry + <c>object_count</c> cap; the spawn / edit
/// (skin·alpha·color_main·color_glow·frame·scale·solidity·physics·force·material) / scale-clamp /
/// attach get·set·remove / remove / duplicate copy·paste (clipboard cvar) / claim / info handlers, dispatched
/// exactly like <c>SV_ParseClientCommand</c> via <see cref="HandleCommand"/>; the per-object think (grab-class
/// + owner-UID resync) and touch (velocity-scaled material impact particle + sound) functions; the textual
/// per-map storage save/load serializer (<c>sandbox_ObjectPort_Save/_Load</c>, including the database owner /
/// date columns and the 16-slot attachment layout); and the live <c>SV_StartFrame</c> 5-second auto-save tick,
/// wired to the real <see cref="MutatorHooks.SvStartFrame"/> seam.</para>
///
/// <para>Faithful Base quirks preserved: the <c>solidity</c>→<c>physics</c> fall-through (Base has a missing
/// <c>break;</c> on the solidity case); flood + maxobjects gating shared by spawn and paste; "no UID =
/// editable by anyone"; <c>g_sandbox_editor_free &gt;= 2</c> overriding ownership; and RoundPerfectVector on
/// the scaled bbox (#2742).</para>
///
/// <para>Cross-file seams this file deliberately calls THROUGH rather than reinventing (see the TODOs in the
/// returned work order): the player-view crosshair trace used by <c>object_spawn</c>/<c>sandbox_ObjectEdit_Get</c>
/// is taken via the injectable <see cref="ITraceProvider"/> (a host wires it to the real
/// <c>crosshair_trace_plusvisibletriggers</c>); object→client CSQC networking, the <c>+button8</c> drag system,
/// and the actual <c>sandbox/</c> file IO are host responsibilities surfaced through small delegates
/// (<see cref="ObjectStore"/>) so this mutator stays in <c>XonoticGodot.Common</c> and unit-testable.</para>
/// </summary>
[Mutator]
public sealed class SandboxMutator : MutatorBase
{
    // ---------------- cvars (autocvar_g_sandbox*) — Base defaults from mutators.cfg ----------------
    private int Info => Api.Services is null ? 1 : (int)Api.Cvars.GetFloat("g_sandbox_info");
    private bool ReadOnly => Api.Services is not null && Api.Cvars.GetFloat("g_sandbox_readonly") != 0f;
    private string StorageName => Api.Services is null ? "default" : Or(Api.Cvars.GetString("g_sandbox_storage_name"), "default");
    private float StorageAutosave => Api.Services is null ? 5f : Api.Cvars.GetFloat("g_sandbox_storage_autosave");
    private bool StorageAutoload => Api.Services is null || Api.Cvars.GetFloat("g_sandbox_storage_autoload") != 0f;
    private float EditorFlood => Api.Services is null ? 1f : Api.Cvars.GetFloat("g_sandbox_editor_flood");
    private int EditorMaxObjects => Api.Services is null ? 1000 : (int)Api.Cvars.GetFloat("g_sandbox_editor_maxobjects");
    private int EditorFree => Api.Services is null ? 1 : (int)Api.Cvars.GetFloat("g_sandbox_editor_free");
    private float DistanceSpawn => Api.Services is null ? 200f : Api.Cvars.GetFloat("g_sandbox_editor_distance_spawn");
    private float DistanceEdit => Api.Services is null ? 300f : Api.Cvars.GetFloat("g_sandbox_editor_distance_edit");
    private float ScaleMin => Api.Services is null ? 0.1f : Api.Cvars.GetFloat("g_sandbox_object_scale_min");
    private float ScaleMax => Api.Services is null ? 2f : Api.Cvars.GetFloat("g_sandbox_object_scale_max");
    private float MaterialVelMin => Api.Services is null ? 100f : Api.Cvars.GetFloat("g_sandbox_object_material_velocity_min");
    private float MaterialVelFactor => Api.Services is null ? 0.002f : Api.Cvars.GetFloat("g_sandbox_object_material_velocity_factor");

    /// <summary>QC <c>const float MAX_STORAGE_ATTACHMENTS = 16;</c>.</summary>
    public const int MaxStorageAttachments = 16;

    public SandboxMutator() => NetName = "sandbox";

    // QC REGISTER_MUTATOR(sandbox, expr_evaluate(autocvar_g_sandbox)). g_sandbox is a string cvar; Base runs it
    // through expr_evaluate so "1"/"0" both work — GetFloat != 0 is the equivalent for the shipped "0"/"1".
    public override bool IsEnabled => Api.Services is not null && Api.Cvars.GetFloat("g_sandbox") != 0f;

    // ---------------- world state (QC globals) ----------------
    /// <summary>QC <c>IntrusiveList g_sandbox_objects;</c> — every live sandbox object, parents and children.</summary>
    private readonly List<SandboxObject> _objects = new();

    /// <summary>QC global <c>float object_count;</c> (parents only — see <see cref="ObjectSpawn"/>/<see cref="ObjectRemove"/>).</summary>
    private int _objectCount;

    /// <summary>QC global <c>float autosave_time;</c>.</summary>
    private float _autosaveTime;

    private HookHandler<MutatorHooks.SvStartFrameArgs>? _onStartFrame;

    // ---------------- cross-file seams (host-injected; null in tests / headless) ----------------

    /// <summary>The view-ray trace a host wires to <c>crosshair_trace_plusvisibletriggers</c> /
    /// <c>WarpZone_TraceLine</c>. Returns the world-space endpoint AND the sandbox object hit (or null). Null
    /// provider ⇒ no object can be selected and spawns land at the player origin (headless / test default).</summary>
    public interface ITraceProvider
    {
        /// <summary>Trace forward <paramref name="distance"/> from the player's eye; out the endpoint and the
        /// sandbox object under the crosshair (null if none / out of <paramref name="distance"/>).</summary>
        void TraceForward(Entity player, float distance, out Vector3 endpos, out SandboxObject? hit);
    }

    /// <summary>The per-map text persistence + clipboard a host wires to real <c>sandbox/storage_*.txt</c> file
    /// IO and the <c>cl_sandbox_clipboard</c> stuffcmd. Null ⇒ persistence/clipboard are inert (build still works
    /// in-session, just never saved/loaded — matches "no storage file" Base behavior).</summary>
    public interface IObjectStore
    {
        string? Read(string storageName, string mapName);
        void Write(string storageName, string mapName, string contents);
        /// <summary>QC <c>stuffcmd(player, "set &lt;cvar&gt; ..")</c> — push the serialized object into the client's cvar.</summary>
        void SetClipboard(Entity player, string cvar, string value);
    }

    public ITraceProvider? Trace { get; set; }
    public IObjectStore? Store { get; set; }

    /// <summary>QC <c>print_to(player, ..)</c> — host-overridable so command feedback can reach the client console.</summary>
    public Action<Entity, string>? PrintTo { get; set; }

    public override void Hook()
    {
        // QC MUTATOR_ONADD: fresh object list, seed autosave_time (don't save the first frame), autoload.
        _objects.Clear();
        _objectCount = 0;
        _autosaveTime = Now + StorageAutosave;
        if (StorageAutoload)
            DatabaseLoad();

        _onStartFrame ??= OnStartFrame;
        MutatorHooks.SvStartFrame.Add(_onStartFrame);
    }

    public override void Unhook()
    {
        if (_onStartFrame is not null)
            MutatorHooks.SvStartFrame.Remove(_onStartFrame);
    }

    private float Now => Api.Services is null ? 0f : Api.Clock.Time;

    // ============================================================================================
    //  Object model
    // ============================================================================================

    /// <summary>One sandbox object — QC <c>new(object)</c> with its sandbox-specific fields. Wraps a world
    /// <see cref="Entity"/> for collision/origin when the entity service is available, but also carries the
    /// sandbox-only properties (alpha/scale/colormod/glowmod/material/owner-UID/dates) that the engine Entity
    /// struct does not yet model, so the full build state is faithful regardless of host wiring.</summary>
    public sealed class SandboxObject
    {
        public Entity? Edict;                 // world entity (may be null headless)
        public Vector3 Origin, Angles, Velocity;
        public string Model = "";
        public float Skin, Frame;
        public float Alpha = 1f;
        public Vector3 Colormod, Glowmod;     // color_main / color_glow (QC default '0 0 0')
        public float Scale = 1f;
        public Solid Solid = Solid.BBox;
        public MoveType MoveType = MoveType.Toss;
        public float DamageForceScale = 1f;   // QC .damageforcescale (sandbox "force")
        public bool TakeDamageAim = true;      // QC .takedamage == DAMAGE_AIM (cleared while attached)
        public string? Material;
        public Vector3 Mins, Maxs;

        // ownership / public info (QC .crypto_idfp / .netname / .message / .message2)
        public string CryptoIdfp = "";
        public string OwnerName = "";
        public string Created = "";
        public string Edited = "";
        public Entity? RealOwner;             // resolved each think while the owner is connected

        // attachment (QC .owner / .object_attach / persisted .old_solid/.old_movetype)
        public SandboxObject? Parent;         // QC .owner: the object this is attached to
        public string AttachTag = "";
        public Solid OldSolid = Solid.BBox;
        public MoveType OldMoveType = MoveType.Toss;

        // think / touch / grab
        public int Grab;                      // 0 none / 1 owner / 3 anyone
        public float TouchTimer;
    }

    /// <summary>Live read-only view of all spawned objects (for the host's CSQC networker / drag system / tests).</summary>
    public IReadOnlyList<SandboxObject> Objects => _objects;
    public int ObjectCount => _objectCount;

    // ============================================================================================
    //  sandbox_ObjectSpawn / Remove / Scale / Attach
    // ============================================================================================

    /// <summary>QC <c>sandbox_ObjectSpawn(this, database)</c>. <paramref name="database"/>=true skips the
    /// owner/info/origin setup (the loader supplies them).</summary>
    public SandboxObject ObjectSpawn(Entity? player, bool database)
    {
        var e = new SandboxObject
        {
            TakeDamageAim = true,
            DamageForceScale = 1f,
            Solid = Solid.BBox,       // SOLID_BSP would be best but lags the server badly
            MoveType = MoveType.Toss,
            Frame = 0,
            Skin = 0,
            Material = null,
        };

        if (Api.Services is not null)
        {
            e.Edict = Api.Entities.Spawn();
            e.Edict.ClassName = "object";
            e.Edict.TakeDamage = DamageMode.Aim;
            e.Edict.Solid = Solid.BBox;
            e.Edict.MoveType = MoveType.Toss;
            // QC sandbox_ObjectSpawn settouch(e, sandbox_ObjectFunction_Touch): velocity-scaled material
            // impact fx/sound fire when the object's physics movement touches another entity. The toucher
            // may be any entity (player, world, another object) — only its velocity is read.
            e.Edict.Touch = (self, other) => ObjectTouch(e, other?.Velocity ?? Vector3.Zero);
        }

        _objects.Add(e);

        if (!database)
        {
            // owner via player UID; without one, objects are unsecured (anyone may edit).
            string uid = CryptoIdfpOf(player);
            if (uid != "")
                e.CryptoIdfp = uid;
            else
                Print(player, "^1SANDBOX - WARNING: ^7You spawned an object, but lack a player UID. ^1Your objects are not secured and can be edited by any player!");

            e.OwnerName = NetNameOf(player);
            e.Created = TimeStamp();
            e.Edited = TimeStamp();

            // origin/direction from player eye + view yaw; trace forward distance_spawn.
            if (Trace is not null && player is not null)
            {
                Trace.TraceForward(player, DistanceSpawn, out Vector3 endpos, out _);
                e.Origin = endpos;
            }
            else if (player is not null)
            {
                e.Origin = player.Origin;
            }
            e.Angles = new Vector3(0f, ViewYawOf(player), 0f);
            ApplyEdictTransform(e);
        }

        // TODO[cross-file]: CSQCMODEL_AUTOINIT(e) — object→client networking has no port seam yet.

        ++_objectCount;
        return e;
    }

    /// <summary>QC <c>sandbox_ObjectRemove</c>: detach children, clear any player's attach-selection, delete.</summary>
    public void ObjectRemove(SandboxObject e)
    {
        ObjectAttachRemove(e); // detach child objects

        // unset any player's pending attach-selection pointing at e (QC FOREACH_CLIENT ... object_attach == e)
        var stale = new List<Entity>();
        foreach (var kv in _attachSelection)
            if (kv.Value == e) stale.Add(kv.Key);
        foreach (var p in stale) _attachSelection.Remove(p);

        if (e.Edict is not null && Api.Services is not null)
            Api.Entities.Remove(e.Edict);
        _objects.Remove(e);

        --_objectCount;
    }

    /// <summary>QC <c>sandbox_ObjectEdit_Scale</c>: clamp to [scale_min, scale_max], re-set model, RoundPerfectVector bbox (#2742).</summary>
    public void ObjectEditScale(SandboxObject e, float f)
    {
        e.Scale = f;
        if (e.Scale != 0f)
        {
            e.Scale = System.Math.Clamp(e.Scale, ScaleMin, ScaleMax);
            if (e.Edict is not null && Api.Services is not null)
                Api.Entities.SetModel(e.Edict, e.Model); // reset mins/maxs based on mesh
            e.Mins = RoundPerfectVector(e.Mins * e.Scale);
            e.Maxs = RoundPerfectVector(e.Maxs * e.Scale);
            if (e.Edict is not null && Api.Services is not null)
                Api.Entities.SetSize(e.Edict, e.Mins, e.Maxs);
        }
    }

    /// <summary>QC <c>sandbox_ObjectAttach_Set</c>: attach e to parent on tag s, persisting old solid/movetype.</summary>
    public void ObjectAttachSet(SandboxObject e, SandboxObject parent, string s)
    {
        ObjectAttachRemove(e); // can't attach an attachment

        e.OldSolid = e.Solid;       // persist solidity
        e.OldMoveType = e.MoveType; // persist physics
        e.MoveType = MoveType.Follow;
        e.Solid = Solid.Not;
        e.TakeDamageAim = false;

        e.Parent = parent;
        e.AttachTag = s;
        if (e.Edict is not null)
        {
            e.Edict.MoveType = MoveType.Follow;
            e.Edict.Solid = Solid.Not;
            e.Edict.TakeDamage = DamageMode.No;
        }
        // QC setattachment(e, parent, s): engine tag-follow attach. Api.Models.SetAttachment is the port's
        // setattachment (same seam Follow/DynamicLight/the vehicles use), so the child now genuinely follows the
        // parent's tag/bone each frame. e.owner = parent in QC is modeled by e.Parent above (used by save/info).
        if (e.Edict is not null && parent.Edict is not null && Api.Services is not null)
            Api.Models.SetAttachment(e.Edict, parent.Edict, s ?? "");
    }

    /// <summary>QC <c>sandbox_ObjectAttach_Remove</c>: detach every object attached to e, restoring its persisted state.</summary>
    public void ObjectAttachRemove(SandboxObject e)
    {
        foreach (var it in _objects)
        {
            if (it.Parent != e) continue;

            // QC: vector org = gettaginfo(it, 0); — the child's current world origin while attached (tag 0 is the
            // entity origin). Capture it BEFORE detaching so the child stays where it is rather than snapping back.
            if (it.Edict is not null && Api.Services is not null)
                it.Origin = it.Edict.Origin;

            // QC setattachment(it, NULL, ""): clear the engine tag-follow so the child stops tracking e's tag.
            if (it.Edict is not null && Api.Services is not null)
                Api.Models.SetAttachment(it.Edict, null!, "");

            // objects change origin/angles when detached → apply previous position, kill spin/roll.
            it.Parent = null;
            it.AttachTag = "";
            it.Angles = e.Angles; // don't let detached objects spin or roll
            it.Solid = it.OldSolid;       // restore persisted solidity
            it.MoveType = it.OldMoveType; // restore persisted physics
            it.TakeDamageAim = true;
            if (it.Edict is not null)
            {
                it.Edict.Solid = it.OldSolid;
                it.Edict.MoveType = it.OldMoveType;
                it.Edict.TakeDamage = DamageMode.Aim;
            }
            ApplyEdictTransform(it);
        }
    }

    /// <summary>QC <c>sandbox_ObjectEdit_Get</c>: crosshair-trace an object, range + classname + ownership gated.
    /// permissions=false ⇒ returns the object regardless of edit rights.</summary>
    public SandboxObject? ObjectEditGet(Entity player, bool permissions)
    {
        if (Trace is null) return null; // no view-ray seam ⇒ nothing selectable (headless/test)
        Trace.TraceForward(player, DistanceEdit, out _, out SandboxObject? hit);
        if (hit is null) return null;                 // nothing / not an object / out of range
        if (!permissions) return hit;                 // anyone can act on it
        if (hit.CryptoIdfp == "") return hit;         // spawner had no UID ⇒ editable by anyone
        // object doesn't belong to us, and players can only edit their own on this server
        if (!(hit.RealOwner != player && EditorFree < 2)) return hit;
        return null;
    }

    // ============================================================================================
    //  think / touch
    // ============================================================================================

    /// <summary>QC <c>sandbox_ObjectFunction_Think</c>: assign grab class + resync owner entity from UID.</summary>
    public void ObjectThink(SandboxObject e, IEnumerable<Entity> realClients)
    {
        // grab class: readonly / Sandbox_DragAllowed veto ⇒ 0; owned + free<2 ⇒ 1; else ⇒ 3.
        // QC: if(autocvar_g_sandbox_readonly || MUTATOR_CALLHOOK(Sandbox_DragAllowed, this)) this.grab = 0;
        if (ReadOnly || MutatorHooks.FireSandboxDragAllowed(e.Edict))
            e.Grab = 0;
        else if (EditorFree < 2 && e.CryptoIdfp != "")
            e.Grab = 1; // owner only
        else
            e.Grab = 3; // anyone

        // resync the owner entity from the UID each frame; clear when the player disconnects. Bots can't own.
        e.RealOwner = null;
        foreach (var it in realClients)
        {
            if (CryptoIdfpOf(it) == e.CryptoIdfp && e.CryptoIdfp != "")
            {
                e.RealOwner = it;
                break;
            }
        }

        // TODO[cross-file]: CSQCMODEL_AUTOUPDATE(e) — object networking seam.
    }

    /// <summary>QC <c>sandbox_ObjectFunction_Touch</c>: rate-limited velocity-scaled material impact fx/sound.
    /// The toucher may be any entity (player, world, another object) — only its velocity is read.</summary>
    public void ObjectTouch(SandboxObject e, Vector3 toucherVelocity)
    {
        if (e.Material is null) return;
        if (e.TouchTimer > Now) return;     // don't execute each frame
        e.TouchTimer = Now + 0.1f;

        // the object's velocity is maintained on its live edict by the physics step (the SandboxObject
        // mirror is only authoritative in headless/tests where there is no edict).
        Vector3 objVel = e.Edict is not null ? e.Edict.Velocity : e.Velocity;
        float intensity = objVel.Length() + toucherVelocity.Length();
        if (intensity != 0f) intensity /= 2f; // average the two velocities
        if (!(intensity >= MaterialVelMin)) return; // impact too weak

        intensity -= MaterialVelMin; // start from minimum velocity, not actual velocity
        intensity = System.Math.Clamp(intensity * MaterialVelFactor, 0f, 1f);

        if (e.Edict is not null && Api.Services is not null)
        {
            int variant = (int)System.Math.Ceiling(Rng() * 5.0); // impact_<mat>_1..5.wav
            Api.Sound.Play(e.Edict, SoundChannel.TriggerAuto,
                $"object/impact_{e.Material}_{variant}.wav", 1f * intensity, 1f);
        }

        // QC: Send_Effect_(strcat("impact_", this.material), this.origin, '0 0 0', ceil(intensity * 10));
        // allow a count from 1 to 10. EmitByEffectInfoName is the port's Send_Effect_ (effectinfo-name keyed);
        // when no registered effect carries that name it still queues the request by name (QC's engine-particle
        // fallback path), so this is bit-faithful regardless of which impact_<mat> effects are registered.
        int count = (int)System.Math.Ceiling(intensity * 10.0);
        Vector3 fxOrigin = e.Edict is not null ? e.Edict.Origin : e.Origin;
        EffectEmitter.EmitByEffectInfoName($"impact_{e.Material}", fxOrigin, Vector3.Zero, count);
        OnMaterialEffect?.Invoke(e, count);
    }

    /// <summary>Optional host observer for the impact particle burst (count 1..10), fired alongside the
    /// <see cref="EffectEmitter"/> emission (QC <c>Send_Effect_("impact_&lt;mat&gt;", …)</c>). Kept for tests /
    /// host instrumentation; the live particle now goes through <see cref="EffectEmitter.EmitByEffectInfoName"/>.</summary>
    public Action<SandboxObject, int>? OnMaterialEffect { get; set; }

    // ============================================================================================
    //  port save / load (textual per-object serializer)
    // ============================================================================================

    /// <summary>QC <c>sandbox_ObjectPort_Save</c>: serialize e (+ its children in slots 1..16) to a string.</summary>
    public string ObjectPortSave(SandboxObject e, bool database)
    {
        var slots = new string[MaxStorageAttachments + 1];
        for (int k = 0; k <= MaxStorageAttachments; ++k) slots[k] = "";
        int o = 0;

        foreach (var it in _objects)
        {
            int slot; Solid solidity; MoveType physics; string? tagName = null;
            if (it == e) // main object first
            {
                slot = 0;
                solidity = it.Solid;
                physics = it.MoveType;
            }
            else if (it.Parent == e) // child, in order
            {
                ++o;
                if (o > MaxStorageAttachments) break;
                slot = o;
                solidity = it.OldSolid;   // persisted solidity is the child's normal solidity
                physics = it.OldMoveType; // persisted physics is the child's normal physics
                tagName = it.AttachTag;   // name of the tag this object is attached to
            }
            else continue;

            var sb = slots[slot];
            if (slot != 0)
            {
                sb += string.IsNullOrEmpty(tagName) ? "\"\" " : $"\"{tagName}\" ";
            }
            else if (database)
            {
                sb += $"\"{V9(it.Origin)}\" ";
                sb += $"\"{V9(it.Angles)}\" ";
            }
            // properties stored for all objects
            sb += $"\"{it.Model}\" ";
            sb += $"{F(it.Skin)} ";
            sb += $"{F(it.Alpha)} ";
            sb += $"\"{V9(it.Colormod)}\" ";
            sb += $"\"{V9(it.Glowmod)}\" ";
            sb += $"{F(it.Frame)} ";
            sb += $"{F(it.Scale)} ";
            sb += $"{F((float)solidity)} ";
            sb += $"{F((float)physics)} ";
            sb += $"{F(it.DamageForceScale)} ";
            sb += it.Material is not null ? $"\"{it.Material}\" " : "\"\" ";
            if (database)
            {
                sb += it.CryptoIdfp != "" ? $"\"{it.CryptoIdfp}\" " : "\"\" ";
                sb += $"\"{e.OwnerName}\" ";
                sb += $"\"{e.Created}\" ";
                sb += $"\"{e.Edited}\" ";
            }
            slots[slot] = sb;
        }

        string s = "";
        for (int j = 0; j <= MaxStorageAttachments; ++j)
            if (!string.IsNullOrEmpty(slots[j]))
                s += slots[j] + "; ";
        return s;
    }

    /// <summary>QC <c>sandbox_ObjectPort_Load</c>: deserialize a string into a new object (+ children) and spawn it.</summary>
    public SandboxObject? ObjectPortLoad(Entity? player, string s, bool database)
    {
        string[] parts = TokenizeBySeparator(s, "; ");
        int n = parts.Length;
        SandboxObject? e = null, parent = null;

        for (int i = 0; i < n; ++i)
        {
            string[] tok = TokenizeConsole(parts[i]);
            int idx = -1;
            string Arg() => (++idx < tok.Length) ? tok[idx] : "";

            string tagName = "";
            e = ObjectSpawn(player, database);

            if (i != 0)
            {
                string a = Arg(); tagName = a != "" ? a : "";
            }
            else
            {
                if (database)
                {
                    e.Origin = Stov(Arg());
                    e.Angles = Stov(Arg());
                }
                parent = e;
            }
            string model = Arg();
            e.Model = model;
            if (e.Edict is not null && Api.Services is not null && model != "")
                Api.Entities.SetModel(e.Edict, model);
            e.Skin = Stof(Arg());
            e.Alpha = Stof(Arg());
            e.Colormod = Stov(Arg());
            e.Glowmod = Stov(Arg());
            e.Frame = Stof(Arg());
            ObjectEditScale(e, Stof(Arg()));
            e.Solid = e.OldSolid = (Solid)(int)Stof(Arg());
            e.OldMoveType = (MoveType)(int)Stof(Arg());
            e.MoveType = e.OldMoveType;
            e.DamageForceScale = Stof(Arg());
            string mat = Arg(); e.Material = mat != "" ? mat : null;
            if (database)
            {
                string uid = Arg(); e.CryptoIdfp = uid != "" ? uid : "";
                e.OwnerName = Arg();
                e.Created = Arg();
                e.Edited = Arg();
            }
            ApplyEdictTransform(e);

            if (i != 0 && parent is not null)
                ObjectAttachSet(e, parent, tagName);
        }
        return e;
    }

    // ============================================================================================
    //  database save / load + autosave tick
    // ============================================================================================

    public void DatabaseSave()
    {
        // QC sandbox_Database_Save head: if(MUTATOR_CALLHOOK(Sandbox_SaveAllowed)) return;
        if (MutatorHooks.FireSandboxSaveAllowed()) return;
        if (Store is null) return;

        string ts = TimeStamp();
        var sb = new System.Text.StringBuilder();
        sb.Append($"// sandbox storage \"{StorageName}\" for map \"{MapName()}\" last updated {ts}");
        sb.Append($" containing {F(_objectCount)} objects\n");
        foreach (var it in _objects)
        {
            if (it.Parent is not null) continue; // children persisted with their parent
            sb.Append(ObjectPortSave(it, true));
            sb.Append('\n');
        }
        Store.Write(StorageName, MapName(), sb.ToString());
    }

    public void DatabaseLoad()
    {
        if (Store is null) return;
        string? data = Store.Read(StorageName, MapName());
        if (data is null)
        {
            if (Info > 0)
                Log($"^3SANDBOX - SERVER: ^7could not find storage file for map ^3{MapName()}^7, no objects were loaded");
            return;
        }
        foreach (string lineRaw in data.Split('\n'))
        {
            string line = lineRaw.TrimEnd('\r');
            if (line == "") continue;
            if (line.StartsWith("//", StringComparison.Ordinal)) continue;
            if (line.StartsWith("#", StringComparison.Ordinal)) continue;

            var e = ObjectPortLoad(null, line, true);
            // QC precaches the 5 impact_<material>_N.wav variants on first load. The port has no explicit
            // precache_sound seam (ISoundService precaches lazily on Play); intentionally a no-op here.
        }
        if (Info > 0)
            Log($"^3SANDBOX - SERVER: ^7successfully loaded storage for map ^3{MapName()}");
    }

    // QC MUTATOR_HOOKFUNCTION(sandbox, SV_StartFrame) — 5-second auto-save.
    private bool OnStartFrame(ref MutatorHooks.SvStartFrameArgs args)
    {
        // tick every object's think (grab/owner resync); QC reschedules nextthink=time each frame.
        if (Api.Services is not null)
        {
            var clients = RealClients();
            foreach (var it in _objects)
                ObjectThink(it, clients);
        }

        if (StorageAutosave == 0f) return false;
        if (args.Time < _autosaveTime) return false;
        _autosaveTime = args.Time + StorageAutosave;
        DatabaseSave();
        return true;
    }

    // ============================================================================================
    //  SV_ParseClientCommand dispatcher
    // ============================================================================================

    private readonly Dictionary<Entity, SandboxObject> _attachSelection = new();
    private readonly Dictionary<Entity, float> _objectFlood = new();

    /// <summary>QC <c>MUTATOR_HOOKFUNCTION(sandbox, SV_ParseClientCommand)</c>. Returns true if the command was
    /// the sandbox <c>g_sandbox</c> command (handled), false if it should fall through.
    /// <para>TODO[cross-file]: the port has no SV_ParseClientCommand mutator hook chain yet — a host must route
    /// the client's <c>cmd g_sandbox …</c> (the <c>sandbox</c> alias in commands.cfg, and every DialogSandboxTools
    /// button) to this method. Args mirror QC: argv[0]="g_sandbox", argv[1]=subcommand, argv[2..]=params.</para></summary>
    public bool HandleCommand(Entity player, string[] argv)
    {
        if (argv.Length == 0 || argv[0] != "g_sandbox")
            return false;

        // QC: if(autocvar_g_sandbox_readonly || MUTATOR_CALLHOOK(Sandbox_EditAllowed, player)) { print readonly; return; }
        if (ReadOnly || MutatorHooks.FireSandboxEditAllowed(player))
        {
            Print(player, "^2SANDBOX - INFO: ^7Sandbox mode is active, but in read-only mode. Sandbox commands cannot be used");
            return true;
        }
        if (argv.Length < 2)
        {
            Print(player, "^2SANDBOX - INFO: ^7Sandbox mode is active. For usage information, type 'sandbox help'");
            return true;
        }

        string A(int i) => i < argv.Length ? argv[i] : "";

        switch (argv[1])
        {
            case "help":
                PrintHelp(player);
                return true;

            case "object_spawn":
            {
                if (Now < FloodOf(player))
                {
                    Print(player, $"^1SANDBOX - WARNING: ^7Flood protection active. Please wait ^3{F(FloodOf(player) - Now)} ^7seconds beofore spawning another object");
                    return true;
                }
                _objectFlood[player] = Now + EditorFlood;
                if (_objectCount >= EditorMaxObjects)
                {
                    Print(player, $"^1SANDBOX - WARNING: ^7Cannot spawn any more objects. Up to ^3{F(EditorMaxObjects)} ^7objects may exist at a time");
                    return true;
                }
                if (argv.Length < 3)
                {
                    Print(player, "^1SANDBOX - WARNING: ^7Attempted to spawn an object without specifying a model. Please specify the path to your model file after the 'object_spawn' command");
                    return true;
                }
                if (!ModelExists(A(2)))
                {
                    Print(player, "^1SANDBOX - WARNING: ^7Attempted to spawn an object with a non-existent model. Make sure the path to your model file is correct");
                    return true;
                }
                var e = ObjectSpawn(player, false);
                e.Model = A(2);
                if (e.Edict is not null && Api.Services is not null)
                    Api.Entities.SetModel(e.Edict, A(2));
                if (Info > 0)
                    Log($"^3SANDBOX - SERVER: ^7{NetNameOf(player)} spawned an object at origin ^3{Vtos(e.Origin)}");
                return true;
            }

            case "object_remove":
            {
                var e = ObjectEditGet(player, true);
                if (e is not null)
                {
                    if (Info > 0)
                        Log($"^3SANDBOX - SERVER: ^7{NetNameOf(player)} removed an object at origin ^3{Vtos(e.Origin)}");
                    ObjectRemove(e);
                    return true;
                }
                Print(player, "^1SANDBOX - WARNING: ^7Object could not be removed. Make sure you are facing an object that you have edit rights over");
                return true;
            }

            case "object_duplicate":
                switch (A(2))
                {
                    case "copy":
                    {
                        var e = ObjectEditGet(player, EditorFree != 0); // can we copy objects we can't edit?
                        if (e is not null)
                        {
                            string s = ObjectPortSave(e, false).Replace("\"", "\\\"");
                            Store?.SetClipboard(player, A(3), s);
                            Print(player, "^2SANDBOX - INFO: ^7Object copied to clipboard");
                            return true;
                        }
                        Print(player, "^1SANDBOX - WARNING: ^7Object could not be copied. Make sure you are facing an object that you have copy rights over");
                        return true;
                    }
                    case "paste":
                    {
                        if (Now < FloodOf(player))
                        {
                            Print(player, $"^1SANDBOX - WARNING: ^7Flood protection active. Please wait ^3{F(FloodOf(player) - Now)} ^7seconds beofore spawning another object");
                            return true;
                        }
                        _objectFlood[player] = Now + EditorFlood;
                        if (A(3) == "")
                        {
                            Print(player, "^1SANDBOX - WARNING: ^7No object in clipboard. You must copy an object before you can paste it");
                            return true;
                        }
                        if (_objectCount >= EditorMaxObjects)
                        {
                            Print(player, $"^1SANDBOX - WARNING: ^7Cannot spawn any more objects. Up to ^3{F(EditorMaxObjects)} ^7objects may exist at a time");
                            return true;
                        }
                        var e = ObjectPortLoad(player, A(3), false);
                        Print(player, "^2SANDBOX - INFO: ^7Object pasted successfully");
                        if (Info > 0 && e is not null)
                            Log($"^3SANDBOX - SERVER: ^7{NetNameOf(player)} pasted an object at origin ^3{Vtos(e.Origin)}");
                        return true;
                    }
                }
                return true;

            case "object_attach":
                switch (A(2))
                {
                    case "get":
                    {
                        var e = ObjectEditGet(player, true);
                        if (e is not null)
                        {
                            _attachSelection[player] = e;
                            Print(player, "^2SANDBOX - INFO: ^7Object selected for attachment");
                            return true;
                        }
                        Print(player, "^1SANDBOX - WARNING: ^7Object could not be selected for attachment. Make sure you are facing an object that you have edit rights over");
                        return true;
                    }
                    case "set":
                    {
                        if (!_attachSelection.TryGetValue(player, out var sel) || sel is null)
                        {
                            Print(player, "^1SANDBOX - WARNING: ^7No object selected for attachment. Please select an object to be attached first.");
                            return true;
                        }
                        var e = ObjectEditGet(player, true);
                        if (e is not null)
                        {
                            ObjectAttachSet(sel, e, A(3));
                            _attachSelection.Remove(player);
                            Print(player, "^2SANDBOX - INFO: ^7Object attached successfully");
                            if (Info > 1)
                                Log($"^3SANDBOX - SERVER: ^7{NetNameOf(player)} attached objects at origin ^3{Vtos(e.Origin)}");
                            return true;
                        }
                        Print(player, "^1SANDBOX - WARNING: ^7Object could not be attached to the parent. Make sure you are facing an object that you have edit rights over");
                        return true;
                    }
                    case "remove":
                    {
                        var e = ObjectEditGet(player, true);
                        if (e is not null)
                        {
                            ObjectAttachRemove(e);
                            Print(player, "^2SANDBOX - INFO: ^7Child objects detached successfully");
                            if (Info > 1)
                                Log($"^3SANDBOX - SERVER: ^7{NetNameOf(player)} detached objects at origin ^3{Vtos(e.Origin)}");
                            return true;
                        }
                        Print(player, "^1SANDBOX - WARNING: ^7Child objects could not be detached. Make sure you are facing an object that you have edit rights over");
                        return true;
                    }
                }
                return true;

            case "object_edit":
            {
                if (A(2) == "")
                {
                    Print(player, "^1SANDBOX - WARNING: ^7Too few parameters. You must specify a property to edit");
                    return true;
                }
                var e = ObjectEditGet(player, true);
                if (e is not null)
                {
                    switch (A(2))
                    {
                        case "skin": e.Skin = Stof(A(3)); break;
                        case "alpha": e.Alpha = Stof(A(3)); break;
                        case "color_main": e.Colormod = Stov(A(3)); break;
                        case "color_glow": e.Glowmod = Stov(A(3)); break;
                        case "frame": e.Frame = Stof(A(3)); break;
                        case "scale": ObjectEditScale(e, Stof(A(3))); break;
                        case "solidity":
                            switch (A(3))
                            {
                                case "0": e.Solid = Solid.Trigger; if (e.Edict is not null) e.Edict.Solid = Solid.Trigger; break; // non-solid
                                case "1": e.Solid = Solid.BBox; if (e.Edict is not null) e.Edict.Solid = Solid.BBox; break;       // solid
                            }
                            // FAITHFUL Base quirk: solidity case has NO break; ⇒ falls through into physics.
                            goto case "physics";
                        case "physics":
                            switch (A(3))
                            {
                                case "0": e.MoveType = MoveType.None; if (e.Edict is not null) e.Edict.MoveType = MoveType.None; break;  // static
                                case "1": e.MoveType = MoveType.Toss; if (e.Edict is not null) e.Edict.MoveType = MoveType.Toss; break;  // movable
                                case "2": e.MoveType = MoveType.Physics; if (e.Edict is not null) e.Edict.MoveType = MoveType.Physics; break;  // physical (MOVETYPE_PHYSICS = 32)
                            }
                            break;
                        case "force": e.DamageForceScale = Stof(A(3)); break;
                        case "material":
                            if (A(3) != "")
                            {
                                // QC precaches object/impact_<mat>_1..5.wav here; the port precaches lazily on Play.
                                e.Material = A(3);
                            }
                            else e.Material = null;
                            break;
                        default:
                            Print(player, "^1SANDBOX - WARNING: ^7Invalid object property. For usage information, type 'sandbox help'");
                            return true;
                    }
                    e.Edited = TimeStamp();
                    if (Info > 1)
                        Log($"^3SANDBOX - SERVER: ^7{NetNameOf(player)} edited property ^3{A(2)} ^7of an object at origin ^3{Vtos(e.Origin)}");
                    return true;
                }
                Print(player, "^1SANDBOX - WARNING: ^7Object could not be edited. Make sure you are facing an object that you have edit rights over");
                return true;
            }

            case "object_claim":
            {
                if (CryptoIdfpOf(player) == "")
                {
                    Print(player, "^1SANDBOX - WARNING: ^7You do not have a player UID, and cannot claim objects");
                    return true;
                }
                var e = ObjectEditGet(player, true);
                if (e is not null)
                {
                    string pn = NetNameOf(player);
                    if (e.OwnerName != pn)
                    {
                        e.OwnerName = pn;
                        Print(player, "^2SANDBOX - INFO: ^7Object owner name updated");
                    }
                    if (e.CryptoIdfp == CryptoIdfpOf(player))
                    {
                        Print(player, "^2SANDBOX - INFO: ^7Object is already yours, nothing to claim");
                        return true;
                    }
                    e.CryptoIdfp = CryptoIdfpOf(player);
                    Print(player, "^2SANDBOX - INFO: ^7Object claimed successfully");
                }
                Print(player, "^1SANDBOX - WARNING: ^7Object could not be claimed. Make sure you are facing an object that you have edit rights over");
                return true;
            }

            case "object_info":
            {
                var e = ObjectEditGet(player, false);
                if (e is not null)
                {
                    switch (A(2))
                    {
                        case "object":
                            Print(player, $"^2SANDBOX - INFO: ^7Object is owned by \"^7{e.OwnerName}^7\", created \"^3{e.Created}^7\", last edited \"^3{e.Edited}^7\"");
                            return true;
                        case "mesh":
                        {
                            // QC: for(i = 1; i <= FOR_EACH_TAG(e); ++i) sb = strcat(sb, "^3", gettaginfo_name, "^7, ");
                            string tags = "";
                            if (MeshTagNamesProvider is not null)
                                foreach (string tag in MeshTagNamesProvider(e.Model))
                                    tags += $"^3{tag}^7, ";
                            Print(player, $"^2SANDBOX - INFO: ^7Object mesh is \"^3{e.Model}^7\" at animation frame ^3{F(e.Frame)} ^7containing the following tags: {tags}");
                            return true;
                        }
                        case "attachments":
                        {
                            string s = ""; int j = 0;
                            foreach (var it in _objects)
                            {
                                if (it.Parent != e) continue;
                                ++j;
                                s += $"^1attachment {F(j)}^7 has mesh \"^3{it.Model}^7\" at animation frame ^3{F(it.Frame)}";
                                s += $"^7 and is attached to bone \"^5{it.AttachTag}^7\", ";
                            }
                            if (j != 0)
                                Print(player, $"^2SANDBOX - INFO: ^7Object contains the following ^1{F(j)}^7 attachment(s): {s}");
                            else
                                Print(player, "^2SANDBOX - INFO: ^7Object contains no attachments");
                            return true;
                        }
                    }
                }
                Print(player, "^1SANDBOX - WARNING: ^7No information could be found. Make sure you are facing an object");
                return true;
            }

            default:
                Print(player, "Invalid command. For usage information, type 'sandbox help'");
                return true;
        }
    }

    private void PrintHelp(Entity player)
    {
        Print(player, "You can use the following sandbox commands:");
        Print(player, "^7\"^2object_spawn ^3models/foo/bar.md3^7\" spawns a new object in front of the player, and gives it the specified model");
        Print(player, "^7\"^2object_remove^7\" removes the object the player is looking at. Players can only remove their own objects");
        Print(player, "^7\"^2object_duplicate ^3value^7\" duplicates the object, if the player has copying rights over the original");
        Print(player, "^3copy value ^7- copies the properties of the object to the specified client cvar");
        Print(player, "^3paste value ^7- spawns an object with the given properties. Properties or cvars must be specified as follows; eg1: \"0 1 2 ...\", eg2: \"$cl_cvar\"");
        Print(player, "^7\"^2object_attach ^3property value^7\" attaches one object to another. Players can only attach their own objects");
        Print(player, "^3get ^7- selects the object you are facing as the object to be attached");
        Print(player, "^3set value ^7- attaches the previously selected object to the object you are facing, on the specified bone");
        Print(player, "^3remove ^7- detaches all objects from the object you are facing");
        Print(player, "^7\"^2object_edit ^3property value^7\" edits the given property of the object. Players can only edit their own objects");
        Print(player, "^3skin value ^7- changes the skin of the object");
        Print(player, "^3alpha value ^7- sets object transparency");
        Print(player, "^3colormod \"value_x value_y value_z\" ^7- main object color");
        Print(player, "^3glowmod \"value_x value_y value_z\" ^7- glow object color");
        Print(player, "^3frame value ^7- object animation frame, for self-animated models");
        Print(player, "^3scale value ^7- changes object scale. 0.5 is half size and 2 is double size");
        Print(player, "^3solidity value ^7- object collisions, 0 = non-solid, 1 = solid");
        Print(player, "^3physics value ^7- object physics, 0 = static, 1 = movable, 2 = physical");
        Print(player, "^3force value ^7- amount of force applied to objects that are shot");
        Print(player, "^3material value ^7- sets the material of the object. Default materials are: metal, stone, wood, flesh");
        Print(player, "^7\"^2object_claim^7\" sets the player as the owner of the object, if they have the right to edit it");
        Print(player, "^7\"^2object_info ^3value^7\" shows public information about the object");
        Print(player, "^3object ^7- prints general information about the object, such as owner and creation / editing date");
        Print(player, "^3mesh ^7- prints information about the object's mesh, including skeletal bones");
        Print(player, "^3attachments ^7- prints information about the object's attachments");
        Print(player, "^7The ^1drag object ^7key can be used to grab and carry objects. Players can only grab their own objects");
    }

    // ============================================================================================
    //  helpers
    // ============================================================================================

    private float FloodOf(Entity p) => _objectFlood.TryGetValue(p, out float t) ? t : 0f;

    private void Print(Entity? player, string msg)
    {
        if (player is not null && PrintTo is not null) PrintTo(player, msg);
    }

    private static void Log(string msg) { /* QC LOG_INFO — host may surface; intentionally quiet headless */ _ = msg; }

    private void ApplyEdictTransform(SandboxObject e)
    {
        if (e.Edict is null || Api.Services is null) return;
        Api.Entities.SetOrigin(e.Edict, e.Origin);
        e.Edict.Angles = e.Angles;
    }

    private bool ModelExists(string path)
    {
        // QC fexists(argv(2)). Use the host seam when available; permissive headless (host validates).
        if (string.IsNullOrEmpty(path)) return false;
        // QC fexists(argv(2)): when a host wires the asset-VFS check, enforce the Base non-existent-model
        // rejection; without one (headless / tests) stay permissive so in-session builds still work.
        return ModelExistsProvider is null ? true : ModelExistsProvider(path);
    }

    private IReadOnlyList<Entity> RealClients()
    {
        // QC FOREACH_CLIENT(IS_PLAYER && IS_REAL_CLIENT) — no player-roster type in Common; the host supplies the
        // live real-client list. Prefer the pull-based provider (wired in GameWorld, matching Warmup/Voting/Bans
        // .Roster) so think resyncs owners + clears on disconnect each frame without a per-frame push; fall back to
        // the SetRealClients snapshot (tests) when no provider is wired.
        return RealClientsProvider?.Invoke() ?? _realClients;
    }
    private IReadOnlyList<Entity> _realClients = Array.Empty<Entity>();
    /// <summary>Host wires the live real (non-bot) client roster source for owner-UID resync (QC FOREACH_CLIENT).
    /// Pull-based to match the codebase's <c>.Roster = () =&gt; Clients.Players</c> seams.</summary>
    public Func<IReadOnlyList<Entity>>? RealClientsProvider { get; set; }
    /// <summary>Host injects the live real (non-bot) client list for owner-UID resync (test/snapshot form).</summary>
    public void SetRealClients(IReadOnlyList<Entity> clients) => _realClients = clients;

    // crypto_idfp / netname / v_angle accessors — no port field yet; host overrides via these delegates.
    public Func<Entity?, string>? CryptoIdfpProvider { get; set; }
    public Func<Entity?, string>? NetNameProvider { get; set; }
    public Func<Entity?, float>? ViewYawProvider { get; set; }

    /// <summary>QC <c>fexists(model)</c> — a host-wired model-file-existence check (the engine has no fexists on
    /// any <c>Api.*</c> seam; a host wires this to the asset VFS). Null ⇒ permissive (headless / tests), so the
    /// build still works in-session; when wired, object_spawn rejects a non-existent model path like Base.</summary>
    public Func<string, bool>? ModelExistsProvider { get; set; }

    /// <summary>QC <c>FOR_EACH_TAG(e)</c> — a host-wired enumeration of an object's model tag (bone) names for
    /// <c>object_info mesh</c>. Null ⇒ no tag list (the mesh subcommand prints the header only, current behavior).
    /// A host wires this to the engine model-tag table (ModelService.ModelDef.Tags).</summary>
    public Func<string, IReadOnlyList<string>>? MeshTagNamesProvider { get; set; }

    private string CryptoIdfpOf(Entity? p) => p is null ? "" : (CryptoIdfpProvider?.Invoke(p) ?? "");
    private string NetNameOf(Entity? p) => p is null ? "" : (NetNameProvider?.Invoke(p) ?? p.NetName);
    private float ViewYawOf(Entity? p) => p is null ? 0f : (ViewYawProvider?.Invoke(p) ?? p.Angles.Y);

    private static string Or(string s, string fallback) => string.IsNullOrEmpty(s) ? fallback : s;
    private static double Rng() => _rng.NextDouble();
    private static readonly Random _rng = new();

    private static string MapName() => Api.Services is null ? "" : Api.Cvars.GetString("mapname");

    private static string TimeStamp() => DateTime.Now.ToString("dd-MM-yyyy HH:mm:ss", CultureInfo.InvariantCulture);

    // QC ftos: integers print without a decimal point, else trimmed.
    private static string F(float f) =>
        f == System.Math.Floor(f) && !float.IsInfinity(f)
            ? ((long)f).ToString(CultureInfo.InvariantCulture)
            : f.ToString("0.######", CultureInfo.InvariantCulture);

    // QC sprintf("%.9v", v) — 9-decimal space-separated vector.
    private static string V9(Vector3 v) =>
        $"{v.X.ToString("0.000000000", CultureInfo.InvariantCulture)} " +
        $"{v.Y.ToString("0.000000000", CultureInfo.InvariantCulture)} " +
        $"{v.Z.ToString("0.000000000", CultureInfo.InvariantCulture)}";

    // QC vtos(v) — "x y z".
    private static string Vtos(Vector3 v) =>
        $"{F(v.X)} {F(v.Y)} {F(v.Z)}";

    private static float Stof(string s) =>
        float.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out float f) ? f : 0f;

    private static Vector3 Stov(string s)
    {
        string t = s.Trim().Trim('\'', '"');
        string[] p = t.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
        float x = p.Length > 0 ? Stof(p[0]) : 0f;
        float y = p.Length > 1 ? Stof(p[1]) : 0f;
        float z = p.Length > 2 ? Stof(p[2]) : 0f;
        return new Vector3(x, y, z);
    }

    // QC tokenizebyseparator(s, "; ") — split on the literal separator, dropping trailing empties.
    private static string[] TokenizeBySeparator(string s, string sep)
    {
        var list = new List<string>();
        int start = 0;
        while (true)
        {
            int idx = s.IndexOf(sep, start, StringComparison.Ordinal);
            if (idx < 0) { if (start < s.Length) list.Add(s.Substring(start)); break; }
            list.Add(s.Substring(start, idx - start));
            start = idx + sep.Length;
        }
        return list.ToArray();
    }

    // QC tokenize_console — whitespace split honoring "double-quoted" tokens.
    private static string[] TokenizeConsole(string s)
    {
        var list = new List<string>();
        int i = 0;
        while (i < s.Length)
        {
            while (i < s.Length && char.IsWhiteSpace(s[i])) ++i;
            if (i >= s.Length) break;
            if (s[i] == '"')
            {
                ++i; int st = i;
                while (i < s.Length && s[i] != '"') ++i;
                list.Add(s.Substring(st, i - st));
                if (i < s.Length) ++i; // skip closing quote
            }
            else
            {
                int st = i;
                while (i < s.Length && !char.IsWhiteSpace(s[i])) ++i;
                list.Add(s.Substring(st, i - st));
            }
        }
        return list.ToArray();
    }

    // QC RoundPerfectVector (#2742) — round each component to the nearest 1/8 to dodge float-precision bbox drift.
    private static Vector3 RoundPerfectVector(Vector3 v)
    {
        static float R(float f) => (float)(System.Math.Round(f * 8.0) / 8.0);
        return new Vector3(R(v.X), R(v.Y), R(v.Z));
    }
}
