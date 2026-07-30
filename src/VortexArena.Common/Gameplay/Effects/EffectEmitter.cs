// Server-side particle-effect emission — the C# successor to QuakeC's Send_Effect / pointparticles /
// trailparticles (common/effects/all.qc, effect.qh) and the te_* temp-effect builtins.
//
// On the server, emitting an effect just queues a request describing "play effect E at origin O with
// velocity V and count N, optionally tinted, optionally except player P". The networking + rendering
// layer (CSQC today, the Godot client later) consumes these and turns the effect name into a particle
// number. We record requests into a swappable sink so server game code (damage, ctf, items) can already
// call emission and tests can assert on it.
//
// The actual server->client wire encoding (the EFF_NET_* byte protocol from all.qc's Net_Write_Effect /
// the net_effect NET_HANDLE) is reproduced exactly in EffectNetProtocol below: a registry-id, the
// location vector, an extraflags byte, then conditional velocity / colour-min / colour-max bytes and the
// count byte for point effects. A real networking sink wraps EffectNetProtocol.Encode; the recording sink
// keeps the structured request so headless tests stay readable.

using System.Numerics;
using XonoticGodot.Common.Framework;

namespace XonoticGodot.Common.Gameplay;

/// <summary>One queued effect emission — the data Send_Effect_Except would network (effect.qh fields).</summary>
public readonly struct EffectRequest
{
    /// <summary>The effect to play (null only if an unknown name was requested; see <see cref="EffectName"/>).</summary>
    public readonly Effect? Effect;

    /// <summary>The effectinfo name actually requested — kept even when <see cref="Effect"/> is null (engine-fallback path).</summary>
    public readonly string EffectName;

    public readonly Vector3 Origin;
    public readonly Vector3 Velocity;

    /// <summary>Particle count for point effects; ignored for trail effects (which sweep Origin..Velocity-end).</summary>
    public readonly int Count;

    /// <summary>Optional per-emission tint range (QC eent_net_color_min/max). Zero vectors = "no override".</summary>
    public readonly Vector3 ColorMin;
    public readonly Vector3 ColorMax;

    /// <summary>Recipient to exclude (QC Send_Effect_Except ignore), e.g. the shooter for their own muzzleflash. Null = everyone.</summary>
    public readonly Entity? Except;

    public bool IsTrail => Effect is { IsTrail: true };

    public EffectRequest(Effect? effect, string effectName, Vector3 origin, Vector3 velocity, int count,
        Vector3 colorMin, Vector3 colorMax, Entity? except)
    {
        Effect = effect;
        EffectName = effectName;
        Origin = origin;
        Velocity = velocity;
        Count = count;
        ColorMin = colorMin;
        ColorMax = colorMax;
        Except = except;
    }

    public override string ToString()
    {
        string name = Effect is null ? $"\"{EffectName}\"(engine)" : Effect.Name;
        string kind = IsTrail ? "trail" : $"x{Count}";
        return $"effect {name} {kind} @ {Origin}";
    }
}

/// <summary>
/// The server-side EFF_NET_* byte protocol — a faithful port of <c>Net_Write_Effect</c> /
/// the <c>net_effect</c> handler in <c>common/effects/all.qc</c>. <see cref="Encode"/> produces the exact
/// byte sequence the CSQC reader consumes; a networking <see cref="IEffectSink"/> can call it directly so
/// the wire format is established now and the Godot client can read it unchanged.
///
/// Wire layout (matching QC, after the net-message header + registered-effect id which the transport
/// frames): location vector, an <c>extraflags</c> byte, then — gated by the flags — the velocity vector,
/// the colour-min triple (each component <c>rint(bound(0, 16*c, 255))</c>), the colour-max triple, and
/// finally (point effects only, i.e. not a trail) the count byte.
/// </summary>
public static class EffectNetProtocol
{
    // QC: const int EFF_NET_* = BIT(n) (common/effects/all.qc).
    public const int NetVelocity = 1 << 0;  // EFF_NET_VELOCITY
    public const int NetColorMin = 1 << 1;  // EFF_NET_COLOR_MIN
    public const int NetColorMax = 1 << 2;  // EFF_NET_COLOR_MAX
    public const int NetColorSame = 1 << 3; // EFF_NET_COLOR_SAME (min == max optimisation)

    /// <summary>
    /// Sentinel "registered effect id" the transport writes in place of a real registry id when the request is
    /// the engine-fallback by-name path (QC <c>Send_Effect_</c> → <c>__pointparticles(_particleeffectnum(name))</c>):
    /// no registered <see cref="Effect"/> carries the name, so the wire carries the effectinfo NAME instead and the
    /// receiver resolves it through its own effectinfo.txt catalog (DP's <c>_particleeffectnum</c> equivalent).
    /// 0xFFFF is safe: the registry is BITS(8) (max 256 ids), so a real id never collides.
    /// </summary>
    public const int ByNameSentinelId = 0xFFFF;

    /// <summary>
    /// Compute the extraflags byte from a request's velocity/colour, exactly as Net_Write_Effect does
    /// (velocity present; colour-min present; the min==max "same" optimisation, else colour-max present).
    /// </summary>
    public static int ExtraFlags(in EffectRequest r)
    {
        int flags = 0;
        if (r.Velocity != Vector3.Zero) flags |= NetVelocity;
        if (r.ColorMin != Vector3.Zero) flags |= NetColorMin;
        if (r.ColorMin != Vector3.Zero && r.ColorMin == r.ColorMax) flags |= NetColorSame;
        else if (r.ColorMax != Vector3.Zero) flags |= NetColorMax;
        return flags;
    }

    /// <summary>QC colour quantisation: <c>rint(bound(0, 16 * c, 255))</c>, one byte per component.</summary>
    public static byte QuantizeColor(float component)
    {
        float scaled = 16f * component;
        if (scaled < 0f) scaled = 0f;
        else if (scaled > 255f) scaled = 255f;
        return (byte)System.MathF.Round(scaled);
    }

    /// <summary>
    /// Encode a request into the EFF_NET body. The leading <see cref="EffectRequest.Effect"/> registry id
    /// is written by the transport (QC <c>WriteRegistered(Effects, …)</c>); this writes everything after.
    /// Returns null for a non-networkable request (no effect, or a count-0 point effect — the same guards
    /// Send_Effect_Except applies before it ever builds the net entity).
    /// </summary>
    public static byte[]? Encode(in EffectRequest r)
    {
        if (r.Effect is null) return null;
        bool trail = r.Effect.IsTrail;
        if (!trail && r.Count == 0) return null;

        var bytes = new List<byte>(20);
        EncodeBody(bytes, r, trail);
        return bytes.ToArray();
    }

    /// <summary>
    /// Encode the EFF_NET body for the engine-fallback by-name path (QC <c>Send_Effect_</c> →
    /// <c>__pointparticles(_particleeffectnum(name), …)</c>): no registered <see cref="Effect"/>, so the transport
    /// writes <see cref="ByNameSentinelId"/> and this body, which carries the EFF_NET payload (origin / extraflags /
    /// velocity / colours / count) for a POINT effect — <c>__pointparticles</c> is always a point spawn. The name
    /// itself is framed by the transport (it owns the leading id/name slot, like <c>WriteRegistered</c>). Returns
    /// null for the same drop the registered path applies (no name, or a count-0 point effect).
    /// </summary>
    public static byte[]? EncodeByName(in EffectRequest r)
    {
        if (r.Effect is not null) return null;          // not the by-name path
        if (string.IsNullOrEmpty(r.EffectName)) return null;
        if (r.Count == 0) return null;                  // point effect with no count → drop (Send_Effect_Except guard)

        var bytes = new List<byte>(20);
        EncodeBody(bytes, r, trail: false);             // __pointparticles is always a point effect
        return bytes.ToArray();
    }

    private static void EncodeBody(List<byte> bytes, in EffectRequest r, bool trail)
    {
        WriteVector(bytes, r.Origin);

        int flags = ExtraFlags(r);
        bytes.Add((byte)flags);

        if ((flags & NetVelocity) != 0)
            WriteVector(bytes, r.Velocity);

        if ((flags & NetColorMin) != 0)
        {
            bytes.Add(QuantizeColor(r.ColorMin.X));
            bytes.Add(QuantizeColor(r.ColorMin.Y));
            bytes.Add(QuantizeColor(r.ColorMin.Z));
        }
        if ((flags & NetColorMax) != 0)
        {
            bytes.Add(QuantizeColor(r.ColorMax.X));
            bytes.Add(QuantizeColor(r.ColorMax.Y));
            bytes.Add(QuantizeColor(r.ColorMax.Z));
        }

        if (!trail)
            bytes.Add((byte)(r.Count & 0xFF)); // WriteByte(channel, eent_net_count)
    }

    // DarkPlaces WriteVector sends 3 little-endian 32-bit floats (the engine coord precision).
    private static void WriteVector(List<byte> dst, Vector3 v)
    {
        dst.AddRange(BitConverter.GetBytes(v.X));
        dst.AddRange(BitConverter.GetBytes(v.Y));
        dst.AddRange(BitConverter.GetBytes(v.Z));
    }
}

/// <summary>
/// Receives effect emissions. The default <see cref="EffectEmitter.RecordingSink"/> buffers them in memory;
/// the host swaps in a networking sink (encoding via <see cref="EffectNetProtocol.Encode"/> and excluding
/// <see cref="EffectRequest.Except"/>) once CSQC/Godot wiring exists. (Analogue of Send_Effect's body.)
/// </summary>
public interface IEffectSink
{
    void Emit(in EffectRequest request);
}

/// <summary>
/// The ambient effect-emission entry point — the C# stand-in for QC's pointparticles/trailparticles macros
/// and te_* builtins. Server gameplay calls these; the active <see cref="Sink"/> decides what happens.
/// </summary>
public static class EffectEmitter
{
    /// <summary>Buffers emissions in memory; useful for the headless server and tests until networking lands.</summary>
    public sealed class RecordingSink : IEffectSink
    {
        private readonly List<EffectRequest> _log = new();
        public IReadOnlyList<EffectRequest> Log => _log;
        public int Count => _log.Count;
        public void Emit(in EffectRequest request) => _log.Add(request);
        public void Clear() => _log.Clear();
        public EffectRequest Last => _log[^1];
    }

    /// <summary>The default recording sink (also exposed typed so callers/tests can read <see cref="RecordingSink.Log"/>).</summary>
    public static readonly RecordingSink Recorder = new();

    /// <summary>The active sink. Defaults to <see cref="Recorder"/>; the host replaces it with a networking sink.</summary>
    public static IEffectSink Sink { get; set; } = Recorder;

    // =====================================================================================
    //  Registry-effect emission (Send_Effect / pointparticles / trailparticles)
    // =====================================================================================

    /// <summary>
    /// Emit a registered effect at <paramref name="origin"/> (QC <c>Send_Effect</c> / <c>pointparticles</c>).
    /// For point effects, <paramref name="count"/> particles are spawned; for trail effects the count is
    /// ignored and <paramref name="velocity"/> is the trail's end point (QC trailparticles convention).
    /// No-ops on a null effect, or on a point effect with count 0 (matches Send_Effect_Except's guards).
    /// </summary>
    public static void Emit(Effect? effect, Vector3 origin, Vector3 velocity = default, int count = 1,
        Entity? except = null)
        => Emit(effect, origin, velocity, count, Vector3.Zero, Vector3.Zero, except);

    /// <summary>Emit a registered effect with an explicit tint range (QC color_min/color_max).</summary>
    public static void Emit(Effect? effect, Vector3 origin, Vector3 velocity, int count,
        Vector3 colorMin, Vector3 colorMax, Entity? except = null)
    {
        if (effect is null) return;                          // QC: if (!eff) return;
        if (!effect.IsTrail && count == 0) return;           // QC: point effect has no count -> drop
        // (perf 2.1) fx.emit: the listen host's in-process effect mirror runs INSIDE the sink — a cold
        // per-effect first-use build here lands in the emitting gameplay scope (the mp.weapon melt hunt).
        using (XonoticGodot.Common.Diagnostics.Prof.Sample("fx.emit"))
            Sink.Emit(new EffectRequest(effect, effect.NetName, origin, velocity, count, colorMin, colorMax, except));
    }

    /// <summary>Emit by stable EFFECT_* name (e.g. "EXPLOSION_BIG"). Convenience over <see cref="Effects.ByName"/>.</summary>
    public static void Emit(string effectName, Vector3 origin, Vector3 velocity = default, int count = 1,
        Entity? except = null)
        => Emit(Effects.ByName(effectName), origin, velocity, count, except);

    /// <summary>
    /// Emit by effectinfo name (QC <c>Send_Effect_</c>). If a registered effect carries that effectinfo
    /// name it's used; otherwise we still queue a request with <see cref="EffectRequest.Effect"/> null so
    /// the layer below can fall back to engine handling (QC's <c>__pointparticles</c> fallback).
    /// </summary>
    public static void EmitByEffectInfoName(string effectInfoName, Vector3 origin, Vector3 velocity = default,
        int count = 1, Entity? except = null)
    {
        var effect = Effects.ByEffectInfoName(effectInfoName);
        if (effect is not null)
        {
            Emit(effect, origin, velocity, count, except);
            return;
        }
        // engine-fallback path: no registered effect, but still record the request by name
        Sink.Emit(new EffectRequest(null, effectInfoName, origin, velocity, count, Vector3.Zero, Vector3.Zero, except));
    }

    /// <summary>Emit a trail effect swept from <paramref name="start"/> to <paramref name="end"/> (QC trailparticles).</summary>
    public static void EmitTrail(Effect? effect, Vector3 start, Vector3 end, Entity? except = null)
        => Emit(effect, start, end, 0, except);

    // =====================================================================================
    //  te_* temp-effect helpers (the DarkPlaces temp-entity builtins used directly in QC,
    //  e.g. te_smallflash, te_explosion, te_spark in common/effects/qc/*). These resolve to a
    //  registered effect by its conventional name and emit a point effect.
    // =====================================================================================

    /// <summary>te_explosion — a generic explosion at <paramref name="origin"/> (maps to EFFECT_TE_EXPLOSION).</summary>
    public static void TeExplosion(Vector3 origin) => Emit("TE_EXPLOSION", origin, default, 1);

    /// <summary>te_tarexplosion — the dark "tar" explosion builtin (QC te_tarexplosion(org)), distinct from
    /// te_explosion; used by Key Hunt for a destroyed key. Maps to the dedicated TE_TAREXPLOSION effectinfo block.</summary>
    public static void TeTarExplosion(Vector3 origin) => Emit("TE_TAREXPLOSION", origin, default, 1);

    /// <summary>te_smallflash — the engine smallflash builtin. In DP this resolves the effectinfo block named
    /// TE_SMALLFLASH (a generic bright flash), NOT any weapon's muzzleflash; mapped to the matching registry
    /// entry so it networks and renders the correct block (fusionreactor heal flash, etc.).</summary>
    public static void TeSmallflash(Vector3 origin) => Emit("TE_SMALLFLASH", origin, default, 1);

    /// <summary>te_spark — a burst of sparks (QC te_spark(org, vel, count)); maps to EFFECT_TE_SPARK.</summary>
    public static void TeSpark(Vector3 origin, Vector3 velocity, int count) => Emit("TE_SPARK", origin, velocity, count);

    /// <summary>
    /// te_csqc_lightningarc(from, to) — a networked jagged lightning bolt (the TE_CSQC_ARC temp-entity from
    /// common/effects/qc/lightningarc.qc, used by the electro combo, Golem lightning zaps and Tesla turret
    /// arcs). Carried on the effect channel as a beam-class emission whose <c>Velocity</c> is the bolt's END
    /// point; the client's beam renderer (BeamRenderer.Arc) draws the crackling bolt between the two points.
    /// </summary>
    public static void TeCsqcLightningArc(Vector3 from, Vector3 to)
        => EmitByEffectInfoName("arc_lightning", from, to, 0);

    /// <summary>
    /// Emit a straight beam between two points, tinted <paramref name="color"/> (0..1 RGB) — the Bumblebee
    /// heal/damage ray (BRG_*) and other rail-style beams. The client's beam renderer draws a cylinder from
    /// <paramref name="from"/> to <paramref name="to"/>. <paramref name="effectName"/> must read as a beam
    /// (contains "beam") so it classifies correctly; a non-electric name keeps it straight (not jagged).
    ///
    /// When the effectinfo name is a registered (trail) Effect — heal_beam/damage_beam are registered for
    /// exactly this — the beam is emitted by registry id so it NETWORKS to remote clients (the colormod rides
    /// the wire eent_net_color_min/max fields, decoded back as the beam tint). Only a truly-unknown name falls
    /// through to the null-Effect record, which the networking sink drops (engine-fallback path; Send_Effect_).
    /// </summary>
    public static void TeBeam(string effectName, Vector3 from, Vector3 to, Vector3 color)
    {
        var effect = Effects.ByEffectInfoName(effectName);
        if (effect is not null)
        {
            // Trail effect, count 0: the velocity is the beam END point (QC beam convention) and the colormod
            // is carried as the min==max colour range (EFF_NET_COLOR_SAME), so the beam networks by id.
            Emit(effect, from, to, 0, color, color);
            return;
        }
        // engine-fallback: no registered effect for this name — record by name (the listen-server in-process
        // mirror can still draw it; the networking sink drops a null-Effect request, matching __pointparticles).
        Sink.Emit(new EffectRequest(null, effectName, from, to, 0, color, color, null));
    }

    /// <summary>
    /// The Bumblebee pilot heal-beam (BRG_*): a straight beam from the gun to the heal target. Base's
    /// bumble_raygun_draw tints the cylinder by colormod = (count ? '1 0 0' : '0 1 0') — i.e. pure green
    /// for the default heal ray (raygun 0). Matched here so the beam colour is bit-faithful to Base.
    /// </summary>
    public static void TeHealBeam(Vector3 from, Vector3 to)
        => TeBeam("heal_beam", from, to, new Vector3(0f, 1f, 0f));

    /// <summary>te_gunshot — the engine gunshot builtin (QC te_gunshot(org, count)). DP resolves the generic
    /// effectinfo block named TE_GUNSHOT (a sparks+smoke+decal impact puff), NOT a specific weapon's impact;
    /// mapped to the matching registry entry so it networks and renders the correct block.</summary>
    public static void TeGunshot(Vector3 origin, int count) => Emit("TE_GUNSHOT", origin, default, count);

    /// <summary>te_blood — a blood spray (QC te_blood(org, vel, count)); maps to EFFECT_BLOOD.</summary>
    public static void TeBlood(Vector3 origin, Vector3 velocity, int count) => Emit("BLOOD", origin, velocity, count);
}
