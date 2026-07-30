using XonoticGodot.Common.Framework;
using XonoticGodot.Common.Gameplay;

namespace XonoticGodot.Server;

/// <summary>
/// Server-side mutator hook chains that have no home in <c>XonoticGodot.Common</c>'s
/// <see cref="MutatorHooks"/> yet — chiefly <c>SV_StartFrame</c>. In QuakeC <c>MUTATOR_CALLHOOK(SV_StartFrame)</c>
/// fires once per server frame from <c>StartFrame()</c> (server/main.qc) after the bot/anticheat frame and
/// before the per-client PostThink/PlayerFrame pass; server-side mutators (e.g. the round-based modes,
/// powerup respawn schedulers, the multijump reset, sandbox) subscribe to it to do their per-frame work.
///
/// Because the event point lives in the server core (this assembly) and not in the Godot-free gameplay
/// layer, the chain is declared here. A mutator or host subscribes via <see cref="SvStartFrame"/>.Add and
/// <see cref="GameWorld"/> fires it from its StartFrame callback.
/// </summary>
public static class ServerHooks
{
    /// <summary>
    /// EV_SV_StartFrame (no QC args) — fired once per server frame from <see cref="GameWorld"/>'s StartFrame,
    /// after bots/anticheat and before the per-client PostThink/PlayerFrame pass. Subscribers do per-frame
    /// server work (round timers driven outside the gametype, scheduled respawns, periodic checks).
    /// </summary>
    public struct SvStartFrameArgs
    {
        /// <summary>The current sim time (QC <c>time</c>) at the top of the frame.</summary>
        public readonly float Time;
        public SvStartFrameArgs(float time) { Time = time; }
    }

    public static readonly HookChain<SvStartFrameArgs> SvStartFrame = new();

    /// <summary>
    /// EV_SV_EndFrame (no QC args) — fired once per server frame from <see cref="GameWorld"/>'s EndFrame,
    /// after the gametype/round/intermission resolution. Mirrors the tail end of the QC server frame for
    /// subscribers that need a post-resolution hook (HUD state push, networking flush).
    /// </summary>
    public struct SvEndFrameArgs
    {
        public readonly float Time;
        public SvEndFrameArgs(float time) { Time = time; }
    }

    public static readonly HookChain<SvEndFrameArgs> SvEndFrame = new();

    /// <summary>Fire <see cref="SvStartFrame"/> for the given sim time (returns true if any handler did).</summary>
    public static bool FireStartFrame(float time)
    {
        var args = new SvStartFrameArgs(time);
        return SvStartFrame.Call(ref args);
    }

    /// <summary>Fire <see cref="SvEndFrame"/> for the given sim time.</summary>
    public static bool FireEndFrame(float time)
    {
        var args = new SvEndFrameArgs(time);
        return SvEndFrame.Call(ref args);
    }
}
