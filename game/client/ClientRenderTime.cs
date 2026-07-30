namespace VortexArena.Game.Client;

/// <summary>
/// The client-side render-animation time scale — the port of how EVERY CSQC/engine-client animation in Base
/// honors <c>slowmo</c>/<c>host_timescale</c> and pause (playtest #30). DP advances the client clock once per
/// frame (<c>cl.time += clframetime</c>, cl_main.c CL_Frame) with <c>clframetime *= cl.movevars_timescale</c>
/// and <c>clframetime = 0</c> while paused — and since ALL CSQC animation (item bob, frame lerps, particles,
/// viewmodel) reads that one clock, everything slows/freezes in lockstep with the sim, for free.
///
/// The port's client-side visual drivers instead advance on raw Godot <c>_Process</c>/<c>_PhysicsProcess</c>
/// deltas (the render loop can't read the fixed-tick server clock per-frame without stalling between ticks —
/// see FaithfulParticleBackend's clock note). This static is the one lockstep point: <c>NetGame._Process</c>
/// publishes its per-frame <c>ResolveTimeScale()</c> (shared cvar on a listen server, the replicated slowmo on
/// a remote client — the same value that already scales the input cadence, #19), and every client animation
/// driver multiplies its local delta by <see cref="Scale"/>. Consumers stay on wall-clock SOURCES (no clock
/// rebasing), so a paused game freezes them exactly like Base without any freeze/leak edge cases.
///
/// Defaults to 1 and is reset to 1 on match shutdown, so the menu / model viewer / a torn-down-paused match
/// never inherit a stale 0.
/// </summary>
public static class ClientRenderTime
{
    /// <summary>Current slowmo factor for client-side visual animation (1 = real time, 0 = paused/frozen).
    /// Written once per frame by <c>NetGame._Process</c>; read by the client animation drivers.</summary>
    public static float Scale = 1f;

    /// <summary>Scale a per-frame delta by the current factor — the one-liner consumers use.</summary>
    public static float ScaleDelta(float dt) => dt * Scale;
}
