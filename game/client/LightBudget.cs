using System;
using System.Collections.Generic;
using Godot;
using VortexArena.Common.Diagnostics;
using VortexArena.Game.Menu;

namespace VortexArena.Game.Client;

/// <summary>
/// The one arbiter that decides, each frame, which dynamic lights are on and which of them may cast a shadow
/// (<b>N6</b>). Every light-owning system registers here instead of deciding for itself.
///
/// <para><b>Why an arbiter rather than a flag per system.</b> Six independent systems create lights — mapper
/// <c>dynlight</c> entities, effectinfo explosion flashes, projectiles, laser endpoints, CSQC player auras and
/// the viewmodel muzzle flash — plus world lights loaded from a <c>.rtlights</c> file. Before this, each capped
/// itself (or didn't), so the only real bound on cost was that nothing cast shadows. An omni shadow is
/// <b>six</b> shadow-map renders of every caster in range; a firefight can have two dozen flashes live at once,
/// and 24 × 6 = 144 shadow renders is how you turn a 2 ms frame into a slideshow. So shadows are a scarce
/// resource handed out by rank, not a property a light gets to claim.</para>
///
/// <para><b>The ranking</b> approximates screen-space importance: a light's contribution falls off with
/// distance and grows with radius and brightness, so <c>energy · range² / max(range, distance)²</c> orders
/// them sensibly — a big bright light across the room outranks a dim one just behind you, and a light you are
/// standing inside outranks everything. DarkPlaces spends its effort in the same place for the same reason
/// (<c>r_shadow_culllights_pvs</c>, <c>r_shadow_culllights_trace</c>, and the
/// <c>r_shadow_shadowmapping_precision</c> size LOD); it renders few lights well rather than many badly.</para>
///
/// <para><b>Cvars</b> (DarkPlaces names where DarkPlaces has one):
/// <list type="bullet">
///   <item><c>r_shadow_realtime_dlight</c> — master gate for dynamic lights. 0 hides them all. Default 1,
///   matching Xonotic's med-and-above presets.</item>
///   <item><c>r_shadow_realtime_dlight_shadows</c> — may dynamic lights cast? Default <b>0</b>, matching every
///   Xonotic preset below ultimate. This is the expensive switch.</item>
///   <item><c>r_shadow_dlight_shadow_budget</c> — how many may cast at once when the above is on. Port-only;
///   DarkPlaces bounds the cost by culling and shadowmap LOD instead, which is a bigger machine than this.</item>
///   <item><c>r_shadow_dlight_max</c> — hard cap on simultaneously VISIBLE dynamic lights, ranked the same
///   way. 0 = unlimited. Port-only, and the thing that keeps a firefight bounded.</item>
/// </list></para>
///
/// <para>Registration is explicit rather than a tree scan: the owning systems already track their nodes, and a
/// per-frame walk of the client world to re-find lights would cost more than the arbitration.</para>
/// </summary>
public sealed partial class LightBudget : Node
{
    /// <summary>The live instance, set on <see cref="_Ready"/>. Null outside a match (menu, headless).</summary>
    public static LightBudget? Instance { get; private set; }

    /// <summary>What a registered light is for. Only <see cref="Role.World"/> lights are exempt from the
    /// dynamic-light gate — they are the map's own static lighting, not a transient effect.</summary>
    public enum Role
    {
        /// <summary>A transient effect light: explosion flash, projectile glow, muzzle flash, laser endpoint.</summary>
        Dynamic,

        /// <summary>A mapper-placed <c>dynlight</c> entity — dynamic, but persistent and usually decorative.</summary>
        MapLight,

        /// <summary>A static world light loaded from a <c>.rtlights</c> file (F4).</summary>
        World,
    }

    private sealed class Entry
    {
        public Light3D Light = null!;
        /// <summary>Cached instance id: once the node is freed, GetInstanceId() on it is no longer safe to
        /// call, and the reaper still needs the key to drop it from the lookup.</summary>
        public ulong Id;
        public Role Role;
        /// <summary>Whether the owner wants this light visible at all (it still has to pass the gate + cap).</summary>
        public bool OwnerVisible = true;
        /// <summary>Set by the owner when the light must never cast regardless of budget (DP PFLAGS_NOSHADOW).</summary>
        public bool NoShadow;
        /// <summary>(F2) DP rtlight <c>corona</c> intensity. 0 = no flare, which is the default and what
        /// every effect light gets - Xonotic authors coronas on map/world lights only, and its own CSQC code
        /// says of the flame dlight "no PFLAGS_CORONA, it looks bad".</summary>
        public float Corona;
        /// <summary>(F2) DP rtlight <c>coronasizescale</c> - flare radius as a fraction of light radius.</summary>
        public float CoronaSize = 0.25f;
        public float Rank;
    }

    private readonly List<Entry> _lights = new();
    private readonly Dictionary<ulong, Entry> _byId = new();
    private readonly List<Entry> _ranked = new();

    /// <summary>Live counts, for the console/HUD readout and for tests.</summary>
    public int Registered => _lights.Count;
    public int VisibleCount { get; private set; }
    public int ShadowCount { get; private set; }

    public override void _Ready() => Instance = this;

    public override void _ExitTree()
    {
        if (Instance == this)
            Instance = null;
    }

    // =================================================================================================
    //  Registration
    // =================================================================================================

    /// <summary>
    /// Put <paramref name="light"/> under the budget. Idempotent — re-registering an already-known light just
    /// updates its role. The budget does NOT own the node: the caller still frees it, and
    /// <see cref="Unregister"/> (or simply freeing it) drops it from the roster.
    /// </summary>
    public static void Register(Light3D? light, Role role, bool noShadow = false,
                                float corona = 0f, float coronaSize = 0.25f)
    {
        if (light is null || Instance is null || !GodotObject.IsInstanceValid(light))
            return;
        ulong id = light.GetInstanceId();
        if (Instance._byId.TryGetValue(id, out Entry? e))
        {
            e.Role = role;
            e.NoShadow = noShadow;
            e.Corona = corona;
            e.CoronaSize = coronaSize;
            return;
        }
        e = new Entry { Light = light, Id = id, Role = role, NoShadow = noShadow, Corona = corona, CoronaSize = coronaSize };
        Instance._byId[id] = e;
        Instance._lights.Add(e);
    }

    /// <summary>Drop a light from the roster. Freed lights are also reaped automatically each frame.</summary>
    public static void Unregister(Light3D? light)
    {
        if (light is null || Instance is null)
            return;
        ulong id = light.GetInstanceId();
        if (!Instance._byId.Remove(id, out Entry? e))
            return;
        Instance._lights.Remove(e);
    }

    /// <summary>
    /// The owner's own visibility decision — "this flash is alive", "this dynlight is toggled on", "this light
    /// is inside the view's PVS". The budget ANDs it with the gate and the cap. Owners must call this instead
    /// of setting <c>Visible</c> directly, or the two will fight each frame.
    /// </summary>
    public static void SetOwnerVisible(Light3D? light, bool visible)
    {
        if (light is null || !GodotObject.IsInstanceValid(light))
            return;
        // Not registered, or no budget at all (menu, model viewer, headless): honour the owner directly
        // rather than silently dropping the call. Without this fallback a light created before the budget
        // node exists would be stuck at whatever Visible it was constructed with.
        if (Instance is null || !Instance._byId.TryGetValue(light.GetInstanceId(), out Entry? e))
        {
            light.Visible = visible;
            return;
        }
        e.OwnerVisible = visible;
    }

    // =================================================================================================
    //  Per-frame arbitration
    // =================================================================================================

    public override void _Process(double delta)
    {
        using var _prof = FrameProfiler.Scope("lightbudget");

        bool dlightsOn = Cvar("r_shadow_realtime_dlight", 1f) != 0f;
        bool shadowsOn = Cvar("r_shadow_realtime_dlight_shadows", 0f) != 0f;
        int shadowBudget = Math.Max(0, (int)Cvar("r_shadow_dlight_shadow_budget", 4f));
        int visibleCap = Math.Max(0, (int)Cvar("r_shadow_dlight_max", 0f));

        Vector3 eye = ViewOrigin();

        _ranked.Clear();
        VisibleCount = 0;
        ShadowCount = 0;

        for (int i = _lights.Count - 1; i >= 0; i--)
        {
            Entry e = _lights[i];
            if (!GodotObject.IsInstanceValid(e.Light))
            {
                _byId.Remove(e.Id);
                _lights.RemoveAt(i);
                continue;
            }

            // A world light is the map's own lighting and is not a "dynamic light" in DP's sense, so the
            // dynamic gate does not silence it.
            bool gated = !dlightsOn && e.Role != Role.World;
            if (gated || !e.OwnerVisible)
            {
                e.Light.Visible = false;
                continue;
            }

            e.Rank = RankOf(e, eye);
            _ranked.Add(e);
        }

        // Highest rank first. A plain comparison sort: the roster is tens of entries, not thousands, and an
        // insertion-order-stable partial select would cost more to maintain than it saves here.
        _ranked.Sort(static (a, b) => b.Rank.CompareTo(a.Rank));

        for (int i = 0; i < _ranked.Count; i++)
        {
            Entry e = _ranked[i];
            bool visible = visibleCap == 0 || i < visibleCap;
            e.Light.Visible = visible;
            if (!visible)
            {
                e.Light.ShadowEnabled = false;
                continue;
            }
            VisibleCount++;

            bool cast = shadowsOn && !e.NoShadow && ShadowCount < shadowBudget;
            // Only touch the property on a change: ShadowEnabled churn re-allocates the light's shadow atlas
            // slot, so flipping it every frame on a light that is oscillating around the budget edge is worse
            // than either state.
            if (e.Light.ShadowEnabled != cast)
                e.Light.ShadowEnabled = cast;
            if (cast)
                ShadowCount++;
        }
    }

    /// <summary>
    /// Screen-importance proxy: brightness × how much of the view the light plausibly covers. Distance is
    /// floored at the light's own radius so a light you are standing inside cannot divide its way to a huge
    /// rank, and every term is finite for a zero-range light.
    /// </summary>
    private static float RankOf(Entry e, Vector3 eye)
    {
        float range = e.Light switch
        {
            OmniLight3D o => o.OmniRange,
            SpotLight3D s => s.SpotRange,
            _ => 0f,
        };
        // A directional light has no position or range; it is always maximally important.
        if (e.Light is DirectionalLight3D)
            return float.MaxValue;

        float dist = eye.DistanceTo(e.Light.GlobalPosition);
        float d = MathF.Max(range, MathF.Max(dist, 1f));
        float rank = e.Light.LightEnergy * range * range / (d * d);

        // World lights are the map's authored lighting; break ties toward them over a decorative flash, so a
        // tight shadow budget spends itself on the lighting the mapper actually placed.
        if (e.Role == Role.World)
            rank *= 1.5f;
        return rank;
    }

    /// <summary>The current view position — the camera when there is one, else the origin.</summary>
    private Vector3 ViewOrigin()
    {
        Viewport? vp = GetViewport();
        Camera3D? cam = vp?.GetCamera3D();
        return cam is not null && GodotObject.IsInstanceValid(cam) ? cam.GlobalPosition : Vector3.Zero;
    }

    /// <summary>Read a client cvar, honouring an explicit 0 (only an UNSET cvar falls back).</summary>
    private static float Cvar(string name, float fallback)
    {
        string s = MenuState.Cvars.GetString(name);
        return string.IsNullOrWhiteSpace(s) ? fallback : MenuState.Cvars.GetFloat(name);
    }

    /// <summary>
    /// (N2) The lights that are actually lit this frame, with their reach - for the per-entity dynamic-light
    /// probe. Yields the budget OWN view of visibility, so a light the cap hid does not keep contributing.
    /// </summary>
    public IEnumerable<(Light3D Light, float Range)> Lit()
    {
        foreach (Entry e in _ranked)
        {
            if (!GodotObject.IsInstanceValid(e.Light) || !e.Light.Visible)
                continue;
            float range = e.Light switch
            {
                OmniLight3D o => o.OmniRange,
                SpotLight3D s => s.SpotRange,
                _ => 0f,
            };
            if (range > 0f)
                yield return (e.Light, range);
        }
    }

    /// <summary>
    /// (F2) The lights that are visible this frame AND carry a corona, for the flare renderer. Yields the
    /// budget's OWN view of visibility, so a light the cap hid does not keep flaring.
    /// </summary>
    public IEnumerable<(Light3D Light, float Corona, float Size)> Coronas()
    {
        foreach (Entry e in _ranked)
        {
            if (e.Corona <= 0f || !GodotObject.IsInstanceValid(e.Light) || !e.Light.Visible)
                continue;
            yield return (e.Light, e.Corona, e.CoronaSize);
        }
    }

    /// <summary>One-line status for the console (`r_lightbudget`) and the perf HUD.</summary>
    public string Status() =>
        $"lights: {Registered} registered, {VisibleCount} visible, {ShadowCount} casting shadows";
}
