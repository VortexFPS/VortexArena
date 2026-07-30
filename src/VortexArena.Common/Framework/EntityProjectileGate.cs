// Port of qcsrc/server/damage.qh (.spawnshieldtime, rocket/mine detonate-gate role)
//   + common/weapons/weapon/devastator.qc + common/weapons/weapon/minelayer.qc

namespace XonoticGodot.Common.Framework;

/// <summary>
/// The rocket/mine remote-detonate GATE — the role QuakeC overloaded onto the single
/// <c>.spawnshieldtime</c> field (declared once at qcsrc/server/damage.qh, reused per entity-type). On a
/// Devastator rocket / Minelayer mine it gates <c>W_*_RemoteExplode</c>:
/// <list type="bullet">
///   <item><c>&gt;= 0</c> — an absolute sim-time TIMER: remote detonation is allowed once
///     <c>time &gt;= ProjectileDetonateTime</c> (QC <c>devastator.qc:145-146</c> / <c>minelayer.qc:142-145</c>).
///     Seeded to <c>time + detonatedelay</c> at launch when the weapon's <c>detonatedelay &gt;= 0</c>.</item>
///   <item><c>&lt; 0</c> — a PROXIMITY-SAFETY branch: detonation is allowed once the projectile is clear of
///     <c>remote_radius</c> (rocket) / no friend is in <c>remote_radius</c> (mine) — the no-mutator
///     rocket-jump path (QC <c>devastator.qc:147</c> / <c>minelayer.qc:147-158</c>).</item>
/// </list>
///
/// This is the field the Rocket Flying mutator clears: <c>MUTATOR_HOOKFUNCTION(rocketflying, EditProjectile)</c>
/// sets <c>proj.spawnshieldtime = time</c> (sv_rocketflying.qc:14), putting the gate in the timer branch with
/// the timer already elapsed so the rocket/mine can be detonated the instant it is fired.
///
/// Kept SEPARATE from the player spawn-shield (<c>Entity.SpawnShieldExpire</c> in Damage/DamageEntityState.cs)
/// and the moving-brush <c>LTime</c>: QC reused one name for unrelated roles, but the port splits them per-role
/// (a rocket entity has <c>SpawnShieldExpire == 0</c> by default, and gating a rocket on it would both break
/// this mutator and risk the damage pipeline treating the rocket as spawn-shielded).
///
/// Default <c>-1f</c> (NOT 0): the C# <see cref="Entity"/> is pooled/reused, so a stale <c>0f</c> from a prior
/// life would sit in the timer branch with <c>time &gt;= 0</c> always true — allowing instant detonation even
/// WITHOUT the mutator (which would defeat the Devastator's default 0.02s arm window). <c>-1f</c> lands a
/// non-projectile entity harmlessly in the proximity branch; every rocket/mine explicitly re-seeds it at launch.
/// </summary>
public partial class Entity
{
    /// <summary>QC <c>.spawnshieldtime</c> on a rocket/mine: the remote-detonate gate. See the type doc.</summary>
    public float ProjectileDetonateTime = -1f;

    /// <summary>
    /// QC CSQC projectile sub-variant: the bouncing model selector. Base picks the networked CSQC projectile
    /// type from the grenade's <c>type</c> cvar — <c>PROJECTILE_GRENADE</c> for an impact/stick grenade
    /// (type 0/2) vs <c>PROJECTILE_GRENADE_BOUNCING</c> for a bouncing grenade (type 1) — which only differ in
    /// the client model + its sideways tumble (mortar.qc:203-207 / :254-256). The grenade entity itself shares
    /// one classname/netname for both modes (the remote-detonate sweep filters on those), so this flag carries
    /// the bouncing distinction to the entity-network catalog key without disturbing them. Default <c>false</c>
    /// (the non-bouncing model) for every non-grenade projectile.
    /// </summary>
    public bool CsqcProjectileBouncing;
}
