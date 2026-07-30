// Port of qcsrc/common/mutators/mutator/nades/sv_nades.qc:79-176
// (nade_boom — the type-switch detonation dispatcher — and nades_spawn_orb, the shared orb helper used by
// heal/ammo/entrap/veil).
//
// THE BOOM-DISPATCH SEAM (read this — part B depends on it):
// nade_boom in QC is one big switch over the nade type that calls the per-type nade_<type>_boom(). To let
// the per-type boom files be SELF-CONTAINED (part B adds Booms/Nade<Type>Boom.cs with NO edit to this core),
// the switch is replaced by a registry: each boom file declares a class implementing <see cref="INadeBoom"/>
// (with the nade NetName it handles + a Boom(nade) method) and is auto-discovered by reflection, exactly
// like the [Mutator]/[Weapon] registries (GameRegistries). The dispatcher looks the handler up by the nade's
// resolved type and calls it. Part B just drops in the files; nothing here changes.
//
//   // a part-B boom file looks like:
//   public sealed class NadeNapalmBoom : INadeBoom {
//       public string NadeNetName => "napalm";
//       public void Boom(Entity nade) { ... RadiusDamage / spawn fountain / etc ... }
//   }
//
// The "destroyed → normal explosion" and "unknown type → normal" fallbacks are handled here before dispatch
// (QC nade_boom:121-126), so a boom file only implements its own special effect.

using System.Numerics;
using System.Reflection;
using XonoticGodot.Common.Framework;
using XonoticGodot.Common.Gameplay;
using XonoticGodot.Common.Services;

namespace XonoticGodot.Common.Gameplay.Nades;

/// <summary>
/// A per-type nade detonation handler — the C# successor to QuakeC's <c>nade_&lt;type&gt;_boom(entity)</c>.
/// Part B implements one of these per nade type in its own Booms/ file; <see cref="NadeBoomRegistry"/>
/// auto-discovers and dispatches them. <see cref="NadeNetName"/> matches the <see cref="NadeDef.NetName"/>
/// the handler detonates (e.g. "normal", "napalm", "heal").
/// </summary>
public interface INadeBoom
{
    /// <summary>The <see cref="NadeDef.NetName"/> this boom handles (QC the NADE_TYPE_* case it implements).</summary>
    string NadeNetName { get; }

    /// <summary>
    /// Detonate the nade entity (QC <c>nade_&lt;type&gt;_boom(this)</c>): apply the type's effect (radius
    /// damage / spawn an orb / freeze field / teleport / etc.). The dispatcher has already played the boom
    /// sound, cleared the nade's event_damage, and resolved fallbacks; the nade entity is deleted by the
    /// dispatcher AFTER this returns, so a handler that needs to keep the nade (translocate/spawn relocate
    /// the owner) reads what it needs synchronously.
    /// </summary>
    void Boom(Entity nade);
}

/// <summary>
/// The boom-dispatch registry — maps a nade NetName to its <see cref="INadeBoom"/>. Self-populating by
/// reflection (mirrors <see cref="GameRegistries"/>) so the per-type boom files need no central edit.
/// </summary>
public static class NadeBoomRegistry
{
    private static readonly Dictionary<string, INadeBoom> _byName = new(StringComparer.Ordinal);
    private static bool _scanned;

    /// <summary>
    /// Discover every <see cref="INadeBoom"/> in the loaded assemblies and register it by NetName. Idempotent.
    /// Called lazily on the first dispatch (and by <see cref="NadesMutator"/> on enable) so part B's boom
    /// files register without any edit to this core. A handler may also be registered explicitly via
    /// <see cref="Register"/> (tests, or a generated registration replacing the reflection scan later).
    /// </summary>
    public static void EnsureScanned()
    {
        if (_scanned) return;
        _scanned = true;

        foreach (Assembly asm in AppDomain.CurrentDomain.GetAssemblies())
        {
            Type?[] types;
            try { types = asm.GetTypes(); }
            catch (ReflectionTypeLoadException e) { types = e.Types; }
            catch { continue; }

            foreach (Type? t in types)
            {
                if (t is null || t.IsAbstract || t.IsInterface) continue;
                if (!typeof(INadeBoom).IsAssignableFrom(t)) continue;
                INadeBoom? inst;
                try { inst = Activator.CreateInstance(t) as INadeBoom; }
                catch { continue; }
                if (inst is not null)
                    Register(inst);
            }
        }
    }

    /// <summary>Register a boom handler by its <see cref="INadeBoom.NadeNetName"/> (idempotent; last wins via overwrite-guard).</summary>
    public static void Register(INadeBoom boom)
    {
        if (string.IsNullOrEmpty(boom.NadeNetName)) return;
        _byName[boom.NadeNetName] = boom;
    }

    /// <summary>The boom handler for a nade NetName, or null if none is registered (part B not present yet).</summary>
    public static INadeBoom? Get(string netName)
    {
        EnsureScanned();
        return _byName.TryGetValue(netName, out var b) ? b : null;
    }

    /// <summary>Test/teardown support: drop all registered handlers + allow a re-scan.</summary>
    public static void Reset()
    {
        _byName.Clear();
        _scanned = false;
    }

    /// <summary>How many boom handlers are currently registered (used by tests to assert part B landed).</summary>
    public static int Count { get { EnsureScanned(); return _byName.Count; } }
}

/// <summary>
/// The nade detonation entry point — port of <c>nade_boom</c> (sv_nades.qc:114) and the shared
/// <c>nades_spawn_orb</c> (sv_nades.qc:79).
/// </summary>
public static class NadeBoom
{
    /// <summary>
    /// Port of <c>nade_boom(entity this)</c>: resolve the effective nade type (a nade destroyed by lava/void or
    /// the Null type detonates as NORMAL — sv_nades.qc:121-126), play the explosion sound, emit the per-type
    /// colored detonation particle through the networked effect feed (QC <c>Send_Effect_Except</c>), clear
    /// event_damage, dispatch to the per-type <see cref="INadeBoom"/>, then delete the nade.
    /// </summary>
    public static void Detonate(Entity nade)
    {
        // QC: Nade ntype = REGISTRY_GET(Nades, STAT(NADE_BONUS_TYPE, this));
        NadeDef ntype = NadeRegistry.ById(nade.NadeBonusType) ?? NadeRegistry.Null;

        // QC: if (!this.takedamage || ntype == NADE_TYPE_Null) ntype = NADE_TYPE_NORMAL;
        // A nade destroyed by lava/void/etc. (takedamage cleared) or the random sentinel does a plain boom —
        // this prevents weird cases (spawn nade setting your spawn in the void, translocate into the void, …).
        if (nade.TakeDamage == DamageMode.No || ntype.Id == 0)
            ntype = NadeRegistry.Normal ?? NadeRegistry.Null;

        // QC: sound(this, CH_SHOTS, SND_ROCKET_IMPACT, ...). One-shot, host-rendered.
        if (Api.Services is not null)
            Api.Sound.Play(nade, SoundChannel.Auto, "weapons/rocket_impact.wav");

        // QC: the per-type Send_Effect_Except colored boom (sv_nades.qc each nade_<type>_boom emits its own
        // tinted detonation effect). Pushed through the SAME networked effect feed weapons use, with except:null
        // so every client — including the listen-server's local one — shows the colored boom. The per-type boom
        // files keep their own gameplay; this lands the central detonation FX exactly once per boom.
        EmitDetonationFx(ntype, nade.Origin);

        // QC: this.event_damage = func_null; — don't re-enter damage from the boom.
        nade.TakeDamage = DamageMode.No;
        nade.GtEventDamage = null;

        // Dispatch to the per-type boom. If part B hasn't landed that boom yet, fall back to the normal boom
        // (so a thrown nade always at least explodes), and if even that is missing, no-op (server still drops it).
        INadeBoom? boom = NadeBoomRegistry.Get(ntype.NetName)
            ?? NadeBoomRegistry.Get(NadeRegistry.Normal?.NetName ?? "normal");
        boom?.Boom(nade);

        if (Api.Services is not null)
            Api.Entities.Remove(nade);
    }

    /// <summary>
    /// Emit the central per-type colored detonation particle through the networked effect feed
    /// (QC <c>Send_Effect_Except(effect, this.origin, '0 0 0', 1)</c>). The effect name is keyed by the nade's
    /// <see cref="NadeDef.NetName"/>; the per-type <see cref="NadeDef.Color"/> rides as the min==max tint range
    /// (EFF_NET_COLOR_SAME), so the FE tints the boom where it supports it (normal's white = no visible tint).
    /// <c>except:null</c> means EVERY client shows it (incl. the listen-server's local player). No-ops cleanly if
    /// the effect name isn't registered (<see cref="EffectEmitter.Emit(string, Vector3, Vector3, int, Entity?)"/>
    /// resolves to a null effect and drops) — but the chosen names are all faithful registered blocks.
    /// </summary>
    private static void EmitDetonationFx(NadeDef ntype, Vector3 origin)
    {
        // Per-type effect block (faithful EFFECT_* names registered in EffectsList):
        //   normal   -> a generic explosion (white tint = no visible recolor),
        //   napalm   -> the big fire explosion (orange),
        //   ice      -> the blue ice/glass shatter,
        //   heal     -> the red healing puff,  ammo -> the blue ammo-regen puff,
        //   any other type (translocate/spawn/monster/entrap/veil/darkness) -> a generic explosion
        //                 tinted by its own NadeDef.Color.
        string effectName = ntype.NetName switch
        {
            "napalm" => "EXPLOSION_BIG",
            "ice" => "ICEORGLASS",
            "heal" => "HEALING",
            "ammo" => "AMMO_REGEN",
            _ => "EXPLOSION_MEDIUM",
        };

        Vector3 tint = ntype.Color; // QC m_color; zero (Null) emits with no override.
        EffectEmitter.Emit(Effects.ByName(effectName), origin, Vector3.Zero, 1, tint, tint, except: null);
    }

    /// <summary>
    /// Port of <c>nades_spawn_orb(entity this, float orb_lifetime, float orb_rad)</c> (sv_nades.qc:79): place
    /// a SOLID_TRIGGER orb at the nade's origin that lives <paramref name="lifetime"/> seconds, carrying the
    /// nade's type/team/owner. The caller adds the per-type touch fn (heal/ammo/entrap/veil) to the returned
    /// entity. The orb's think drives the ~20 Hz particle gate (<see cref="Entity.NadeShowParticles"/>) and
    /// self-deletes at expiry. Net_LinkEntity (render) is omitted.
    /// </summary>
    public static Entity SpawnOrb(Entity nade, float lifetime, float radius)
    {
        float now = Api.Services is not null ? Api.Clock.Time : 0f;
        Entity orb = Api.Entities.Spawn();
        orb.ClassName = "nade_orb";
        // QC: orb.owner = this.owner; orb.realowner = this.realowner. The port's RealOwner aliases Owner, so the
        // crediting player (QC realowner) IS the orb's Owner. The orb's team follows the thrower's team.
        orb.Owner = nade.Owner;
        orb.Team = nade.Owner?.Team ?? nade.Team;
        Api.Entities.SetOrigin(orb, nade.Origin);

        orb.NadeOrbExpire = now + lifetime;
        orb.Solid = Solid.Trigger;

        // QC sets the orb model + a [-0.5r, 0.5r] hull. Headless: just the hull (model is render-only).
        float half = 0.5f * radius;
        Api.Entities.SetSize(orb, new System.Numerics.Vector3(-half, -half, -half), new System.Numerics.Vector3(half, half, half));

        orb.NadeBonusType = nade.NadeBonusType;
        orb.NadeSpecialTime = now;
        orb.Think = OrbThink;
        orb.NextThink = now;
        return orb;
    }

    /// <summary>Port of <c>nades_orb_think</c> (sv_nades.qc:60): expire the orb, else toggle the ~20 Hz particle gate.</summary>
    private static void OrbThink(Entity orb)
    {
        float now = Api.Services is not null ? Api.Clock.Time : 0f;
        if (now >= orb.NadeOrbExpire)
        {
            Api.Entities.Remove(orb);
            return;
        }
        orb.NextThink = now;
        if (now >= orb.NadeSpecialTime)
        {
            orb.NadeSpecialTime = now + 0.05f;
            orb.NadeShowParticles = true;
        }
        else
            orb.NadeShowParticles = false;
    }
}
