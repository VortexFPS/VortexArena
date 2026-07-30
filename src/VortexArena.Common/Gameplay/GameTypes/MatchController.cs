using XonoticGodot.Common.Services;

namespace XonoticGodot.Common.Gameplay;

/// <summary>
/// A callback the active gametype fires when a kill is scored, so a host/controller can react without the
/// gametype owning the player roster. Mirrors the role QC's obituary plays for the server main loop.
/// </summary>
public interface IMatchEvents
{
    /// <summary>A frag was scored. <paramref name="attacker"/> is null for suicides / world deaths.</summary>
    void OnFrag(Player? attacker, Player victim, string deathType);
}

/// <summary>
/// The thin match-loop glue the host/sim drives — the C# stand-in for the relevant slice of the QC server
/// main loop (StartFrame / the per-frame respawn check in server/client.qc). It owns the active gametype and
/// the player roster, wires spawning to <see cref="SpawnSystem"/>, and on each <see cref="Tick"/> respawns
/// players whose <see cref="Player.RespawnTime"/> has elapsed.
///
/// This is deliberately host-agnostic: it touches only <see cref="XonoticGodot.Common"/> APIs (Api.Clock for
/// time, the gametype's scoring, SpawnSystem for placement), so it runs headless in the deterministic sim
/// and under the Godot host alike.
/// </summary>
public sealed class MatchController : IMatchEvents
{
    private readonly List<Player> _players = new();
    private readonly List<Player> _livePlayersScratch = new();

    /// <summary>The active gametype. Today always <see cref="Deathmatch"/>; typed broadly for future gametypes.</summary>
    public GameType? GameType { get; private set; }

    /// <summary>The active gametype as a <see cref="Deathmatch"/>, or null if a different one is active.</summary>
    public Deathmatch? Deathmatch => GameType as Deathmatch;

    /// <summary>The match roster (players currently in the game, alive or awaiting respawn).</summary>
    public IReadOnlyList<Player> Players => _players;

    /// <summary>True once the active gametype has flagged the frag limit reached.</summary>
    public bool MatchEnded => Deathmatch?.MatchEnded ?? false;

    /// <summary>The current frag leader, per the active gametype.</summary>
    public Player? Leader => Deathmatch?.Leader;

    /// <summary>
    /// Begin a Deathmatch: register the gametype's kill handler and spawn every roster player. Call after the
    /// roster is populated via <see cref="AddPlayer"/> (or pass them here). Idempotent for the gametype hook.
    /// </summary>
    public void ActivateDeathmatch(Deathmatch dm, IEnumerable<Player>? roster = null)
    {
        GameType = dm;
        if (roster is not null)
            foreach (Player p in roster)
                AddPlayer(p);

        dm.Events = this;
        dm.Activate();

        // Initial spawn for everyone already on the roster (QC: PutClientInServer per joining client).
        foreach (Player p in _players)
            Spawn(p);

        dm.RecomputeLeader(_players);
    }

    /// <summary>Tear down the active gametype (unsubscribe its kill handler). Players are left in place.</summary>
    public void Deactivate()
    {
        Deathmatch?.Deactivate();
        if (Deathmatch is not null)
            Deathmatch.Events = null;
    }

    /// <summary>Add a player to the roster (no spawn — call <see cref="Spawn"/> or rely on Activate's initial spawn).</summary>
    public void AddPlayer(Player p)
    {
        if (!_players.Contains(p))
            _players.Add(p);
    }

    /// <summary>Remove a player from the roster (QC: client disconnect).</summary>
    public void RemovePlayer(Player p) => _players.Remove(p);

    /// <summary>Spawn (or respawn) a player: pick a spawnpoint away from the living, then place + load it out.</summary>
    public void Spawn(Player p)
    {
        BuildLivePlayers();
        // [R0c] targetCheck:true — match Base's Spawn_FilterOutBadSpots(..., targetcheck=true) for every spawn
        // (spawnpoints.qc:419); the emergency re-filter re-admits spots if a map rejects them all. Inert on DM/CTF.
        SpawnPoint? sp = SpawnSystem.SelectSpawnPoint(p, _livePlayersScratch, targetCheck: true);
        if (sp is null)
        {
            // QC: spawn failed (no spawnpoints) — retry next tick by leaving the respawn timer armed soon.
            float now = Api.Services is not null ? Api.Clock.Time : 0f;
            p.RespawnTime = now + 1f; // QC: "only retry once a second"
            return;
        }
        SpawnSystem.PutPlayerInServer(p, sp.Value);
    }

    /// <summary>
    /// Advance the match one step. Respawns any dead player whose delay has elapsed (QC StartFrame respawn
    /// check). Safe to call every frame; does nothing surprising once <see cref="MatchEnded"/> is set except
    /// stop respawning, mirroring QC's game_stopped gate.
    /// </summary>
    public void Tick()
    {
        if (GameType is null)
            return;

        float now = Api.Services is not null ? Api.Clock.Time : 0f;

        // Don't respawn once the match has ended (QC: game_stopped blocks PutClientInServer).
        if (!MatchEnded)
        {
            for (int i = 0; i < _players.Count; i++)
            {
                Player p = _players[i];
                if (p.IsAwaitingRespawn(now))
                    Spawn(p);
            }
        }

        // Keep the leader/limit authoritative across the whole roster each tick.
        Deathmatch?.RecomputeLeader(_players);
    }

    /// <summary>IMatchEvents: the gametype already scored + scheduled the respawn; nothing extra needed here.</summary>
    public void OnFrag(Player? attacker, Player victim, string deathType)
    {
        // Hook point for hosts that want kill feed / sounds. The respawn is driven by Tick() off
        // victim.RespawnTime, so no action is required here. (Kept so the gametype has a sink to call.)
    }

    private void BuildLivePlayers()
    {
        _livePlayersScratch.Clear();
        for (int i = 0; i < _players.Count; i++)
        {
            Player p = _players[i];
            if (!p.IsDead)
                _livePlayersScratch.Add(p);
        }
    }
}
