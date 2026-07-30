// Port of qcsrc/common/mutators/mutator/nades/sv_nades.qc — the HELD-nade priming + throw-charge path:
//   spawn_held_nade (530), nade_prime (576), CanThrowNade (618), nades_CheckThrow (625),
//   nades_Clear (656), and the OFFHAND_NADE.offhand_think charge (675-704).
//
// A player primes a nade (charge starts), the held nade tracks in front of them and its charge ramps; on
// release (or the alt-button toggle for the weapon_drop path) the nade is tossed with a force proportional
// to how long it charged. The actual projectile lifecycle is NadeProjectile; this file decides WHICH nade
// to prime (bonus / strength / cvar / client-select), tracks the held nade, and computes the throw force.

using System.Numerics;
using XonoticGodot.Common.Framework;
using XonoticGodot.Common.Math;
using XonoticGodot.Common.Services;

namespace XonoticGodot.Common.Gameplay.Nades;

/// <summary>The held-nade priming + charge-throw logic (QC the spawn_held_nade / nade_prime / CheckThrow set).</summary>
public static class NadeThrow
{
    // mutators.cfg defaults read on the throw path.
    private const float DefLifetime = 3.5f;   // g_nades_nade_lifetime
    private const float DefRefire = 6f;       // g_nades_nade_refire
    private const float DefMinForce = 400f;   // g_nades_nade_minforce
    private const float DefMaxForce = 2000f;  // g_nades_nade_maxforce
    private const float DefSpread = 0.04f;    // g_nades_spread

    /// <summary>
    /// Port of <c>spawn_held_nade(player, nowner, ntime, ntype, pntype)</c> (sv_nades.qc:530): create the
    /// held nade entity (and its first-person fake-nade) of the resolved type, tinted by the player, primed
    /// now with a <paramref name="lifetime"/>-second fuse (it beeps then booms at the end). Sets
    /// <see cref="Entity.Nade"/>/<see cref="Entity.FakeNade"/> on the player. The model/trail are render-only.
    /// </summary>
    public static void SpawnHeldNade(Entity player, Entity? nowner, float lifetime, string ntype, string? pntype)
    {
        if (Api.Services is null) return;

        NadeDef def = NadeRegistry.GetType(ntype);

        Entity n = Api.Entities.Spawn();
        n.ClassName = "nade";
        n.PokenadeType = pntype;
        n.NadeBonusType = def.Id;
        n.Owner = nowner;                  // QC realowner = nowner (RealOwner aliases Owner)
        n.Team = nowner?.Team ?? 0f;

        float now = Api.Clock.Time;
        n.NadeWait = now + MathF.Max(0f, lifetime);        // QC .wait = time + lifetime (the boom deadline)
        n.NextThink = MathF.Max(n.NadeWait - 3f, now);     // QC nextthink = max(wait - 3, time): beep starts 3s before boom
        n.NadeTimePrimed = now;
        n.NadeLifetime = lifetime;
        n.Think = NadeProjectile.Beep;
        // hold nade alpha (veil nade = 0.45). Render-only but cheap to mirror.
        // (the model is render-only and omitted)

        // QC fake_nade: the first-person view nade — render-only; we still track it so toss_nade clears it.
        Entity fn = Api.Entities.Spawn();
        fn.ClassName = "fake_nade";
        fn.Owner = player;
        fn.NextThink = n.NadeWait;
        fn.Think = e => Api.Entities.Remove(e); // SUB_Remove at the boom time (QC fn.nextthink = n.wait)

        player.Nade = n;
        player.FakeNade = fn;
    }

    /// <summary>
    /// Port of <c>nade_prime(entity this)</c> (sv_nades.qc:576): choose which nade to prime — a strength-bonus
    /// nade (infinite while Strength is held), a banked bonus nade (decrementing the count), or the configured
    /// /client-selected default — then spawn the held nade. Bonus-only mode forbids priming without a bonus.
    /// </summary>
    public static void Prime(Entity player)
    {
        if (Api.Services is null) return;

        // QC: bonus_only mode requires a banked bonus nade.
        if (Cvar("g_nades_bonus_only", 0f) != 0f && player.NadeBonus <= 0)
            return;

        // discard any existing held nade
        if (player.Nade is not null) { Api.Entities.Remove(player.Nade); player.Nade = null; }
        if (player.FakeNade is not null) { Api.Entities.Remove(player.FakeNade); player.FakeNade = null; }

        NadeDef ntype;
        string? pntype = player.PokenadeType;

        bool hasStrength = PowerupActive(player, "strength");
        if (hasStrength && Cvar("g_nades_bonus_onstrength", 1f) != 0f)
        {
            // QC: strength → use the bonus type without consuming a bonus (infinite bonus nades).
            ntype = NadeRegistry.ById(player.NadeBonusType) ?? NadeRegistry.Normal ?? NadeRegistry.Null;
        }
        else if (player.NadeBonus >= 1)
        {
            ntype = NadeRegistry.ById(player.NadeBonusType) ?? NadeRegistry.Normal ?? NadeRegistry.Null;
            pntype = player.PokenadeType;
            --player.NadeBonus;
        }
        else
        {
            // QC: client-select uses the client cvar; otherwise the server default nade type.
            // The headless sim has no per-client cvar, so use the server cvars (g_nades_nade_type).
            ntype = NadeRegistry.FromString(CvarStr("g_nades_nade_type", "1"));
            if (ntype.Id == 0) ntype = NadeRegistry.Normal ?? NadeRegistry.Null;
            pntype = CvarStr("g_nades_pokenade_monster_type", "zombie");
        }

        SpawnHeldNade(player, player, Cvar("g_nades_nade_lifetime", DefLifetime), ntype.NetName, pntype);
    }

    /// <summary>
    /// Port of <c>CanThrowNade(entity this)</c> (sv_nades.qc:618): a live, on-foot player may throw nades
    /// while g_nades is on and their weapon isn't locked. (Vehicle/weaponLocked are approximated by the
    /// available flags; the headless sim has no vehicle on a plain player.)
    /// </summary>
    public static bool CanThrowNade(Entity player)
    {
        if (Api.Services is null) return false;
        if (Cvar("g_nades", 0f) == 0f) return false;
        if ((player.Flags & EntFlags.Client) == 0) return false;
        if (player.DeadState != DeadFlag.No) return false;
        return true;
    }

    /// <summary>
    /// Port of <c>nades_CheckThrow(entity this)</c> (sv_nades.qc:625): the weapon_drop path — first press
    /// primes (if past the refire), a second press (≥1s after priming) throws with charge-scaled force.
    /// Used by the impulse/weapon-drop bind; the offhand (+hook) path uses <see cref="OffhandThink"/>.
    /// </summary>
    public static void CheckThrow(Entity player)
    {
        if (!CanThrowNade(player)) return;

        if (player.Nade is null)
        {
            player.NadeAltButton = true;
            if (Now() > player.NadeRefire)
            {
                Prime(player);
                player.NadeRefire = Now() + Cvar("g_nades_nade_refire", DefRefire);
            }
        }
        else
        {
            player.NadeAltButton = false;
            if (Now() >= player.Nade.NadeTimePrimed + 1f)
            {
                Vector3 dir = ChargeDir(player, 0.75f, 0.2f, 0.05f, out float force);
                NadeProjectile.Toss(player, true, dir * force, 0f);
            }
        }
    }

    /// <summary>
    /// Port of <c>OFFHAND_NADE.offhand_think</c> (sv_nades.qc:675): the +hook offhand path. While the key is
    /// held, prime (once); on release (≥1s after priming) throw with charge-scaled force. Driven each frame
    /// from <see cref="NadesMutator"/> PlayerPreThink, gated by <see cref="Entity.NadeAltButton"/>.
    /// </summary>
    public static void OffhandThink(Entity player, bool keyPressed)
    {
        if (!CanThrowNade(player) || Now() <= player.NadeRefire)
            return;

        if (keyPressed)
        {
            if (player.Nade is null)
                Prime(player);
        }
        else if (player.Nade is not null && Now() >= player.Nade.NadeTimePrimed + 1f)
        {
            Vector3 dir = ChargeDir(player, 0.7f, 0.2f, 0.1f, out float force);
            NadeProjectile.Toss(player, false, dir * force, 0f);
        }
    }

    /// <summary>
    /// Port of <c>nades_Clear(entity player)</c> (sv_nades.qc:656): drop the held + fake nade and reset the
    /// HUD charge. Used on observer/disconnect/reset and when re-priming.
    /// </summary>
    public static void Clear(Entity player)
    {
        if (Api.Services is null) return;
        if (player.Nade is not null) Api.Entities.Remove(player.Nade);
        if (player.FakeNade is not null) Api.Entities.Remove(player.FakeNade);
        player.Nade = null;
        player.FakeNade = null;
        player.NadeTimer = 0f;
    }

    // ===================================================================================================
    //  charge math (shared by CheckThrow + OffhandThink) — QC the _force / dir block
    // ===================================================================================================

    /// <summary>
    /// QC the charge-force computation (sv_nades.qc:646-650 / 695-699): force ramps from minforce to maxforce
    /// over the nade lifetime since priming; direction is a forward-biased spread cone. The fwd/up/right
    /// weights differ between the weapon-drop path (0.75/0.2/0.05) and the offhand path (0.7/0.2/0.1).
    /// </summary>
    private static Vector3 ChargeDir(Entity player, float fwdW, float upW, float rightW, out float force)
    {
        Vector3 viewAngles = player.ViewAngles == Vector3.Zero ? player.Angles : player.ViewAngles;
        QMath.AngleVectors(viewAngles, out Vector3 forward, out Vector3 right, out Vector3 up);

        float held = Now() - (player.Nade?.NadeTimePrimed ?? Now());
        held /= Cvar("g_nades_nade_lifetime", DefLifetime);
        float minF = Cvar("g_nades_nade_minforce", DefMinForce);
        float maxF = Cvar("g_nades_nade_maxforce", DefMaxForce);
        force = minF + held * (maxF - minF);

        Vector3 dir = forward * fwdW + up * upW + right * rightW;
        // QC W_CalculateSpread(dir, g_nades_spread, g_projectiles_spread_style, false).
        return WeaponFiring.CalculateSpread(dir, Cvar("g_nades_spread", DefSpread), mustNormalize: false);
    }

    // ===================================================================================================
    //  helpers
    // ===================================================================================================

    private static bool PowerupActive(Entity e, string name)
    {
        var def = StatusEffectsCatalog.ByName(name);
        return def is not null && StatusEffectsCatalog.Has(e, def);
    }

    private static float Now() => Api.Services is not null ? Api.Clock.Time : 0f;

    private static float Cvar(string name, float fallback)
    {
        if (Api.Services is null) return fallback;
        string s = Api.Cvars.GetString(name);
        return string.IsNullOrEmpty(s) ? fallback : Api.Cvars.GetFloat(name);
    }

    private static string CvarStr(string name, string fallback)
    {
        if (Api.Services is null) return fallback;
        string s = Api.Cvars.GetString(name);
        return string.IsNullOrEmpty(s) ? fallback : s;
    }
}
