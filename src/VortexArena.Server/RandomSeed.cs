namespace XonoticGodot.Server;

/// <summary>
/// QC <c>randomseed</c> (server/world.qc:594-614): a tiny networked entity carrying a server-rolled random seed,
/// re-rolled every 5 seconds and broadcast to every client so a predicting client can reproduce the same
/// deterministic spread/effect stream the server used (the seed feeds <see cref="XonoticGodot.Common.Math.Prandom"/>
/// via <c>psrandom</c>).
///
/// <para>This is the SERVER producer half (QC <c>RandomSeed_Think</c> / <c>RandomSeed_Spawn</c>): a per-instance
/// <see cref="Current"/> int re-rolled on the 5-second <c>nextthink</c> period, ticked once per server frame from
/// <see cref="XonoticGodot.Server.GameWorld.OnStartFrame"/> and instantiated in <c>Boot</c>. The wire half rides the
/// existing <c>SendMatchState</c> packet (ServerNet) — the smallest diff that reaches every accepted peer; the
/// client consumes it into a per-instance deterministic RNG (ClientNet).</para>
///
/// <para>Per-instance (NOT a static) so a listen server's server-side seed is owned by the server world and never
/// collides with the client's decoded copy — matching the per-context <see cref="XonoticGodot.Common.Math.Prandom"/>
/// split. There is no current client EFFECT consuming the seed (the registry confirms it), so networking it is
/// additive and has nothing to desync; the seam is shipped now so a future deterministic effect can use it.</para>
/// </summary>
public sealed class RandomSeed
{
    /// <summary>QC the 5-second <c>nextthink</c> re-roll period (<c>this.nextthink = time + 5</c>, world.qc:602).</summary>
    public const float RerollPeriod = 5f;

    // The seed itself is rolled from a plain process RNG (QC uses the engine random(); the seed VALUE need not be
    // bit-exact with Base because no client effect consumes the stream yet — only the per-context Prandom seeded
    // FROM it must agree across server/client, and that's the shared int below). One instance per server world.
    private readonly System.Random _roll = new();

    private float _nextThink;

    /// <summary>QC <c>this.cnt</c>: the current 16-bit seed (0..65535), re-rolled every <see cref="RerollPeriod"/>
    /// seconds. Broadcast on the match-state channel; the client seeds its deterministic RNG from this.</summary>
    public int Current { get; private set; }

    public RandomSeed()
    {
        // QC RandomSeed_Spawn calls the think once immediately (sets the first seed + arms nextthink).
        Reroll(0f);
    }

    /// <summary>QC <c>RandomSeed_Think</c>: ticked once per server frame; re-rolls <see cref="Current"/> when the
    /// 5-second period elapses (the SendFlags |= 1 in QC is the port's match-state re-send, which already detects the
    /// changed value). <paramref name="now"/> is the server sim time.</summary>
    public void Tick(float now)
    {
        if (now >= _nextThink)
            Reroll(now);
    }

    private void Reroll(float now)
    {
        // QC: this.cnt = bound(0, floor(random() * 65536), 65535).
        Current = System.Math.Clamp((int)System.Math.Floor(_roll.NextDouble() * 65536.0), 0, 65535);
        _nextThink = now + RerollPeriod;
    }
}
