#if XG_BOTPLAYER
namespace XonoticGodot.Game.Net;

/// <summary>
/// BOT-PLAYER HARNESS — compile-gated, see <c>Directory.Build.props</c> (<c>XgBotPlayer</c>).
///
/// <para>Hands the LOCAL HUMAN player slot to a bot brain so an unattended run drives the real player code.
/// Spectating a bot (<c>cl_bench_spectate</c>) exercises rendering and the sim, but the player pipeline —
/// input sampling, the client predictor, input encode/ack, the reconcile against authority, client fire
/// prediction, weapon-switch and viewmodel state — never runs, because nothing is producing local input.
/// This produces that input.</para>
///
/// <para>The player stays a REAL client: <c>IsBot</c> is false, so <c>GameWorld</c> keeps sourcing its
/// command from the net <c>InputProvider</c>. The brain only supplies what a pair of hands would, and the
/// command still travels sample → predict → encode → ENet → server authority → snapshot → reconcile. The
/// brain itself thinks on the SIM thread (<c>BotPopulation.ThinkBotPlayer</c>, per tick) because it reads
/// world state; this side only samples the published result.</para>
///
/// <para><b>Why a compile flag and not a cvar.</b> A brain steering a human player is, mechanically, an
/// aimbot. A cvar could be set by a config, a server, or a stray console line, so the gate has to be one
/// that cannot be flipped at runtime at all: the code is simply absent from any build that did not opt in,
/// and even an opted-in build stays dormant until <c>--bot-player</c> is passed. Never define
/// <c>XgBotPlayer</c> for a release or export build.</para>
/// </summary>
internal static class BotPlayerMode
{
    /// <summary>Set once at boot from <c>--bot-player</c>. Never written after startup.</summary>
    public static bool Requested;

    /// <summary>Brain skill (QC bot skill rungs, 0..10); <c>--bot-player &lt;skill&gt;</c>.</summary>
    public static float Skill = 5f;
}
#endif
