using XonoticGodot.Common.Framework;

namespace XonoticGodot.Common.Gameplay;

// Port of QuakeC's minigame framework (common/minigames/minigame.qh, minigames.qc, sv_minigames.qc) —
// the SERVER-SIDE game logic only. CSQC rendering (cl_minigames*, the *_hud_board / *_hud_status draws)
// and the entity-networking layer (Net_LinkEntity, SendFlags, MSLE read/write) are deliberately omitted.
//
// QC model -> C# model
// --------------------
//   minigame descriptor (REGISTER_MINIGAME)         -> Minigame (abstract, IRegistered, one per game type)
//      .netname / .message                          ->   NetName / DisplayName
//      int <id>_server_event(mg, "start"/"join"/…)  ->   Start / Join / Part / Move / End virtuals
//   minigame session entity                          -> MinigameSession (board, players, turn, winner)
//      .minigame_flags (turn-type | turn-team bits)  ->   TurnType + CurrentTeam (decoded fields)
//   minigame_board_piece entities (.netname,.team)   -> Board: tile-name -> BoardCell(team)
//   minigame_player entities (.team, client link)    -> MinigamePlayer
//
// Tile naming is reproduced exactly from QC (minigames.qc): a tile id is "<letter><number>", where letter
// is the column ('a','b','c',… = 0,1,2,…) and number is the 1-based row digit (so "a1" = column 0, row 0).
// The row axis counts from the BOTTOM. See MinigameBoard for the encode/decode helpers.

/// <summary>
/// Turn phase of a session — the high bits of QC's <c>minigame_flags</c> (e.g. TTT_TURN_PLACE / _WIN /
/// _DRAW). Concrete games may not use every value; the common ones are modelled here so the framework can
/// drive shared "is the game over?" logic.
/// </summary>
public enum MinigameTurn
{
    /// <summary>Game has not started / waiting for players (QC PONG_STATUS_WAIT).</summary>
    Waiting = 0,
    /// <summary>It is a player's turn to act — place/move (QC *_TURN_PLACE / *_TURN_MOVE).</summary>
    Play = 1,
    /// <summary>A player has won (QC *_TURN_WIN). <see cref="MinigameSession.Winner"/> holds who.</summary>
    Win = 2,
    /// <summary>Board is full / no moves possible with no winner (QC *_TURN_DRAW).</summary>
    Draw = 3,
}

/// <summary>The result a concrete <see cref="Minigame.Move"/> reports back to the framework.</summary>
public enum MoveResult
{
    /// <summary>Illegal/ignored move; session state unchanged (QC: the move function just returns).</summary>
    Invalid = 0,
    /// <summary>Legal move applied; play continues (turn passes to the next team).</summary>
    Accepted = 1,
    /// <summary>Legal move applied and it ended the game (win or draw); see <see cref="MinigameSession.TurnType"/>.</summary>
    GameOver = 2,
}

/// <summary>
/// One player in a session — the C# successor to QC's <c>minigame_player</c> entity. <see cref="Team"/> is
/// the QC team number (1..n, or <see cref="MinigameSession.SpectatorTeam"/> for spectators). <see cref="Slot"/>
/// mirrors QC's <c>minigame_playerslot</c> (0 == AI, &gt;0 == a connected client); kept as a back-link so
/// game logic can tell humans from AI without depending on the networking layer.
/// </summary>
public sealed class MinigamePlayer
{
    public int Team;
    /// <summary>0 = AI-controlled, &gt;0 = (client index + 1). QC <c>minigame_playerslot</c>.</summary>
    public int Slot;
    /// <summary>Per-player scratch score (QC e.g. <c>pong_score</c> / won-match counter). Game-specific.</summary>
    public int Score;
    /// <summary>Optional engine entity for the controlling client (null for AI). Not required by game logic.</summary>
    public Entity? Client;

    public bool IsAi => Slot == 0;

    public MinigamePlayer(int team, int slot = 0, Entity? client = null)
    {
        Team = team;
        Slot = slot;
        Client = client;
    }
}

/// <summary>
/// One occupied tile on a board — the C# successor to QC's <c>minigame_board_piece</c>. Carries the owning
/// <see cref="Team"/> and an optional <see cref="Flags"/> word for game-specific per-piece state (QC
/// <c>minigame_flags</c> on the piece).
/// </summary>
public sealed class BoardCell
{
    public int Team;
    public int Flags;

    public BoardCell(int team, int flags = 0)
    {
        Team = team;
        Flags = flags;
    }
}

/// <summary>
/// Tile-name math reproduced from QuakeC (common/minigames/minigames.qc). All grid minigames address cells
/// by string id; these helpers convert between ids and (letter=column, number=row) indices, both 0-based.
/// Row 0 is the bottom row, exactly as QC. Pure functions, no allocation beyond the produced strings.
/// </summary>
public static class MinigameTiles
{
    /// <summary>Column index of a tile id ('a' -> 0). QC <c>minigame_tile_letter</c>.</summary>
    public static int Letter(string id) => id.Length == 0 ? 0 : char.ToLowerInvariant(id[0]) - 'a';

    /// <summary>Row index of a tile id (0-based; "a1" -> 0). QC <c>minigame_tile_number</c>.</summary>
    public static int Number(string id)
    {
        // QC: stof(substring(id,1,-1)) - 1
        if (id.Length < 2) return -1;
        return int.TryParse(id.AsSpan(1), out int n) ? n - 1 : -1;
    }

    /// <summary>Build a tile id from (letter, number) indices, both 0-based. QC <c>minigame_tile_buildname</c>.</summary>
    public static string BuildName(int letter, int number) => $"{(char)('a' + letter)}{number + 1}";

    /// <summary>
    /// Tile id offset by (dx, dy) from <paramref name="startId"/>, wrapping within the grid. QC
    /// <c>minigame_relative_tile</c>. <paramref name="columns"/>/<paramref name="rows"/> bound the wrap.
    /// </summary>
    public static string RelativeTile(string startId, int dx, int dy, int rows, int columns)
    {
        int letter = (Letter(startId) + dx) % columns;
        int number = (Number(startId) + dy) % rows;
        if (letter < 0) letter += columns;
        if (number < 0) number += rows;
        return BuildName(letter, number);
    }
}

/// <summary>
/// Generic grid board shared by the tile-based minigames (TTT, Connect Four, Peg Solitaire). A sparse map
/// of tile id -> <see cref="BoardCell"/>, the C# stand-in for "all <c>minigame_board_piece</c> entities
/// owned by this session". Bounds (<see cref="Columns"/> x <see cref="Rows"/>) let it validate tiles.
/// </summary>
public sealed class MinigameBoard
{
    public int Columns { get; }
    public int Rows { get; }

    private readonly Dictionary<string, BoardCell> _cells = new(StringComparer.Ordinal);

    public MinigameBoard(int columns, int rows)
    {
        Columns = columns;
        Rows = rows;
    }

    /// <summary>Number of occupied tiles (QC piece count, e.g. <c>ttt_npieces</c>).</summary>
    public int PieceCount => _cells.Count;

    /// <summary>All occupied (tile id, cell) pairs.</summary>
    public IReadOnlyDictionary<string, BoardCell> Cells => _cells;

    /// <summary>Is the tile within the grid bounds? (Concrete games may add extra masks — see Peg Solitaire.)</summary>
    public bool InBounds(string? tile)
    {
        if (string.IsNullOrEmpty(tile)) return false;
        int letter = MinigameTiles.Letter(tile);
        int number = MinigameTiles.Number(tile);
        return letter >= 0 && letter < Columns && number >= 0 && number < Rows;
    }

    /// <summary>The cell at a tile, or null if empty (QC <c>*_find_piece</c>).</summary>
    public BoardCell? Find(string tile) => _cells.TryGetValue(tile, out var c) ? c : null;

    /// <summary>Team owning a tile, or 0 if empty. Convenience for the win-checks.</summary>
    public int TeamAt(string tile) => _cells.TryGetValue(tile, out var c) ? c.Team : 0;

    public int TeamAt(int letter, int number) => TeamAt(MinigameTiles.BuildName(letter, number));

    public bool IsEmpty(string tile) => !_cells.ContainsKey(tile);

    /// <summary>Place a piece (overwrites). Returns the new cell.</summary>
    public BoardCell Place(string tile, int team, int flags = 0)
    {
        var cell = new BoardCell(team, flags);
        _cells[tile] = cell;
        return cell;
    }

    /// <summary>Move a piece from one tile to another (Peg Solitaire). No-op if source is empty.</summary>
    public bool MovePiece(string from, string to)
    {
        if (!_cells.TryGetValue(from, out var cell)) return false;
        _cells.Remove(from);
        _cells[to] = cell;
        return true;
    }

    /// <summary>Remove the piece at a tile (the jumped peg). Returns true if one was there.</summary>
    public bool Remove(string tile) => _cells.Remove(tile);

    /// <summary>Clear the whole board (QC: delete all board pieces on "end" / "next match").</summary>
    public void Clear() => _cells.Clear();
}

/// <summary>
/// A running minigame instance — the C# successor to QuakeC's minigame session entity. Holds the players,
/// the board, whose turn it is, and the outcome. The concrete <see cref="Minigame"/> drives state changes
/// through its event methods; this type is mostly data plus small turn helpers.
/// </summary>
public sealed class MinigameSession
{
    /// <summary>QC: team number above max teams used for spectators (255). Equal across all games.</summary>
    public const int SpectatorTeam = 255;

    /// <summary>The game type this session is playing (the QC descriptor).</summary>
    public Minigame Game { get; }

    /// <summary>
    /// QC the session entity's <c>.netname</c> — a unique id "&lt;gameid&gt;_&lt;entnum&gt;" (e.g. "ttt_3")
    /// assigned by <see cref="MinigameSessionManager.Create"/> and used to join/list/network the session.
    /// "" until the manager seats it (a bare session built directly for the rules has no id).
    /// </summary>
    public string NetName { get; set; } = "";

    /// <summary>Joined players in join order (QC <c>minigame_players</c> linked list).</summary>
    public List<MinigamePlayer> Players { get; } = new();

    /// <summary>Shared grid board, for tile-based games. Null for games that keep bespoke state (e.g. Pong).</summary>
    public MinigameBoard? Board { get; set; }

    /// <summary>Current turn phase (decoded from QC <c>minigame_flags</c> turn-type bits).</summary>
    public MinigameTurn TurnType { get; set; } = MinigameTurn.Waiting;

    /// <summary>Whose turn it is (QC team number, low bits of <c>minigame_flags</c>). 0 = nobody/N-A.</summary>
    public int CurrentTeam { get; set; }

    /// <summary>Winning team once <see cref="TurnType"/> is <see cref="MinigameTurn.Win"/>; else 0.</summary>
    public int Winner { get; set; }

    /// <summary>Free word for game-specific session state (QC fields like <c>ttt_nexteam</c>, <c>ttt_ai</c>).</summary>
    public int State;

    /// <summary>Open bag of bespoke per-session objects (e.g. Pong paddles/balls, Snake state).</summary>
    public Dictionary<string, object> Extra { get; } = new(StringComparer.Ordinal);

    public MinigameSession(Minigame game) => Game = game;

    public bool IsOver => TurnType is MinigameTurn.Win or MinigameTurn.Draw;

    public int PlayerCount => Players.Count;

    /// <summary>Find a player by team number (QC: scan minigame_players).</summary>
    public MinigamePlayer? PlayerByTeam(int team)
    {
        foreach (var p in Players)
            if (p.Team == team) return p;
        return null;
    }

    /// <summary>The non-spectator player whose turn it currently is, if any.</summary>
    public MinigamePlayer? CurrentPlayer => CurrentTeam == 0 ? null : PlayerByTeam(CurrentTeam);

    /// <summary>QC <c>minigame_next_team(curr, n)</c>: cycle 1..n.</summary>
    public static int NextTeam(int currentTeam, int teamCount) => currentTeam % teamCount + 1;

    // ---- thin pass-throughs to the descriptor, so callers can drive a session directly ----
    public int Start() => Game.Start(this);
    public int Join(MinigamePlayer player) => Game.Join(this, player);
    public void Part(MinigamePlayer player) => Game.Part(this, player);
    public MoveResult Move(MinigamePlayer player, string move) => Game.Move(this, player, move);
    public void End() => Game.End(this);
}

/// <summary>
/// Base class for a minigame type — the C# successor to QuakeC's <c>REGISTER_MINIGAME(id, nicename)</c>
/// descriptor and its <c>&lt;id&gt;_server_event</c> dispatcher. One singleton instance per game, enrolled
/// into the <see cref="Minigames"/> catalog. Concrete games override the event methods to implement their
/// rules against a <see cref="MinigameSession"/>.
/// </summary>
public abstract class Minigame : IRegistered
{
    public int RegistryId { get; set; }

    /// <summary>Stable id (QC <c>.netname</c>, lower-case), e.g. "ttt", "c4". Used for ordering + lookup.</summary>
    public abstract string NetName { get; }

    /// <summary>Localized display name (QC <c>.message</c>), e.g. "Tic Tac Toe".</summary>
    public abstract string DisplayName { get; }

    public string RegistryName => NetName;

    /// <summary>Max players this game accepts before new joiners become spectators (QC join-event check).</summary>
    public virtual int MaxPlayers => 2;

    /// <summary>Create a fresh session bound to this game (allocates the board if the game uses one).</summary>
    public virtual MinigameSession CreateSession()
    {
        var s = new MinigameSession(this);
        Start(s);
        return s;
    }

    /// <summary>
    /// Begin a session — QC "start" event. Set up the board and the opening turn. Returns true (1) like the
    /// QC event. Default leaves the session waiting.
    /// </summary>
    public virtual int Start(MinigameSession session) => 1;

    /// <summary>
    /// A player wants to join — QC "join" event. Return the team number to assign, or
    /// <see cref="MinigameSession.SpectatorTeam"/> to seat them as a spectator. The caller is expected to
    /// then add the player (with the returned team) to <see cref="MinigameSession.Players"/>.
    /// Default: assign teams 1..MaxPlayers in order, then spectator.
    /// </summary>
    public virtual int Join(MinigameSession session, MinigamePlayer player)
    {
        int n = session.PlayerCount;
        if (n >= MaxPlayers) return MinigameSession.SpectatorTeam;
        // mirror QC: next team after the most-recently-joined player, else team 1
        if (session.Players.Count > 0)
            return MinigameSession.NextTeam(session.Players[^1].Team, MaxPlayers);
        return 1;
    }

    /// <summary>A player leaves — QC "part" event. Default removes them from the player list.</summary>
    public virtual void Part(MinigameSession session, MinigamePlayer player) => session.Players.Remove(player);

    /// <summary>
    /// Apply a move for <paramref name="player"/> — QC "cmd"/"move". <paramref name="move"/> is the
    /// game-specific argument (e.g. a tile id "b2", a column, or a "from to" pair). Returns whether the move
    /// was accepted and whether it ended the game. Default rejects everything.
    /// </summary>
    public virtual MoveResult Move(MinigameSession session, MinigamePlayer player, string move) => MoveResult.Invalid;

    /// <summary>
    /// Recompute / report end-of-game state — the win/draw check. Returns the resulting turn phase and is
    /// also responsible for setting <see cref="MinigameSession.Winner"/> when it returns
    /// <see cref="MinigameTurn.Win"/>. Concrete games call this from <see cref="Move"/>; exposed separately
    /// so tests (and AI) can probe the board. Default: never ends.
    /// </summary>
    public virtual MinigameTurn CheckEnd(MinigameSession session) => session.TurnType;

    /// <summary>Tear a session down — QC "end" event. Default clears the board.</summary>
    public virtual void End(MinigameSession session) => session.Board?.Clear();
}

/// <summary>
/// The minigame catalog — the FOREACH(Minigames, …) target, successor to QC's <c>REGISTRY(Minigames, …)</c>.
/// Self-registering: gameplay seeds it via <see cref="RegisterAll"/> at boot (the lead wires that into
/// GameInit), NOT the attribute/reflection bootstrap.
/// </summary>
public static class Minigames
{
    private static bool _done;

    public static IReadOnlyList<Minigame> All => Registry<Minigame>.All;
    public static int Count => Registry<Minigame>.Count;

    /// <summary>Look up a game by its <see cref="Minigame.NetName"/> (QC <c>minigame_get_descriptor</c>).</summary>
    public static Minigame? ByName(string name) => Registry<Minigame>.ByName(name);

    public static Minigame ById(int id) => Registry<Minigame>.ById(id);

    public static uint Hash => Registry<Minigame>.ContentHash();

    /// <summary>Convenience: create a fresh, started session for the named game (null if unknown).</summary>
    public static MinigameSession? StartNew(string name) => ByName(name)?.CreateSession();

    /// <summary>
    /// Enroll every implemented minigame and fix deterministic ordering. Idempotent; the lead calls it once
    /// from GameInit. Analogue of QC's per-game <c>REGISTER_MINIGAME</c> + the registry sort.
    /// </summary>
    public static void RegisterAll()
    {
        if (_done) return;
        _done = true;

        Registry<Minigame>.Register(new TicTacToe());
        Registry<Minigame>.Register(new ConnectFour());
        Registry<Minigame>.Register(new Pong());
        Registry<Minigame>.Register(new PegSolitaire());
        Registry<Minigame>.Register(new NineMensMorris());
        Registry<Minigame>.Register(new PushPull());
        Registry<Minigame>.Register(new Bulldozer());
        Registry<Minigame>.Register(new Snake());

        Registry<Minigame>.Sort();
    }

    /// <summary>Reset (test support).</summary>
    public static void Reset()
    {
        _done = false;
        Registry<Minigame>.Clear();
    }
}
