using XonoticGodot.Common.Framework;

namespace XonoticGodot.Common.Gameplay;

/// <summary>
/// The server-side lag-compensation hook (port of the <c>antilag_takeback</c> … <c>antilag_restore</c> bracket
/// around a hitscan trace in <c>server/antilag.qc</c>). Hitscan weapon code calls <see cref="Begin"/> before its
/// authoritative trace and <see cref="End"/> after, so other players are momentarily rewound to where the
/// shooter saw them at fire time — making hit registration fair under latency.
///
/// Gameplay lives in <c>XonoticGodot.Common</c> and must not know about the netcode, so this is an ambient hook (the
/// same pattern as <c>EffectEmitter.Sink</c>): the server installs a <see cref="ILagCompensation"/> provider; on
/// a client, in a test, or a bot-only server it stays null and the bracket is a no-op (no rewind — correct,
/// since there is no remote-view latency to compensate for).
/// </summary>
public static class LagComp
{
    // SUPERCONTENTS bits (DP collision.h). Common can't reference the Engine's SuperContents class
    // (Engine depends on Common, not the reverse), so mirror the bit values here exactly as
    // Projectiles.MakeTrigger does. These MUST match XonoticGodot.Engine.Collision.SuperContents.
    private const int SuperContentsSolid  = 0x00000001;
    private const int SuperContentsBody   = 0x02000000;
    private const int SuperContentsCorpse = 0x20000000;

    /// <summary>The active provider (installed by the server's net driver); null = no compensation (no-op).</summary>
    public static ILagCompensation? Provider;

    // [sv-antilag.solidmask.corpse] The shooter whose dphitcontentsmask we widened, and its saved value, so
    // End can restore it. Single-threaded server trace path (the bracket never nests across shooters; the
    // takeback's _lagCompActive guard already forbids re-entrant rewind), so a single saved slot suffices.
    private static Entity? _maskShooter;
    private static int _savedMask;

    /// <summary>
    /// Rewind other players to <paramref name="shooter"/>'s view-time AND temporarily widen the shooter's
    /// hit-contents mask to include CORPSE, so the upcoming trace can hit gibbed/corpse bodies. Pair with
    /// <see cref="End"/>.
    /// </summary>
    public static void Begin(Entity shooter)
    {
        // [sv-antilag.solidmask.corpse] Port of tracebox_antilag_force_wz (antilag.qc:177-180) +
        // W_SetupShot (tracing.qc:44/50): Base temporarily sets the trace SOURCE's dphitcontentsmask to
        // SOLID|BODY|CORPSE so the shot can hit corpses (a live player's own trace mask is SOLID|BODY|
        // PLAYERCLIP, which drops CORPSE). This switch is UNCONDITIONAL in Base — outside the `if(lag)`
        // antilag gate — so it must happen even at g_antilag==0 / for a bot / when no provider is installed,
        // independently of the rewind below. The port's TraceService honors Entity.DpHitContentsMask as an
        // override (TraceService.GenericHitMask), so widening it here makes corpse entities (Solid.Corpse,
        // SuperContents.Corpse) hittable for the duration of the bracket.
        if (shooter is not null && _maskShooter is null)
        {
            _maskShooter = shooter;
            _savedMask = shooter.DpHitContentsMask;
            shooter.DpHitContentsMask = SuperContentsSolid | SuperContentsBody | SuperContentsCorpse;
        }

        Provider?.Begin(shooter);
    }

    /// <summary>Restore the rewound players to their authoritative positions and the shooter's hit mask.</summary>
    public static void End()
    {
        Provider?.End();

        // [sv-antilag.solidmask.corpse] Restore the shooter's original dphitcontentsmask (antilag.qc:196-197).
        if (_maskShooter is not null)
        {
            _maskShooter.DpHitContentsMask = _savedMask;
            _maskShooter = null;
            _savedMask = 0;
        }
    }
}

/// <summary>Server-net-backed lag compensation (rewinds/restores other players around a shooter's trace).</summary>
public interface ILagCompensation
{
    void Begin(Entity shooter);
    void End();
}
