// Port of common/mutators/mutator/superspec/sv_superspec.qc

using System;
using System.Collections.Generic;
using System.Text;
using XonoticGodot.Common.Framework;
using XonoticGodot.Common.Services;

namespace XonoticGodot.Common.Gameplay;

/// <summary>
/// The Super Spectate mutator — port of common/mutators/mutator/superspec/sv_superspec.qc. Gives spectators
/// power-spectating conveniences: auto-switch the follow-cam onto whoever grabs a Strength/Shield powerup,
/// a Mega Health/Armor, or a CTF flag; auto-switch onto a victim's killer (followkiller); pickup-announcement
/// messages with a per-client item filter; and the <c>superspec</c>/<c>autospec</c>/<c>superspec_itemfilter</c>/
/// <c>followpowerup</c>/<c>followstrength</c>/<c>followshield</c> spectator console commands. Enabled by the
/// string cvar <c>g_superspectate</c> (expr_evaluate); Base default is "" (off).
///
/// WHAT IS PORTED IN THIS FILE (faithful to Base):
///   - REGISTER_MUTATOR predicate (<see cref="IsEnabled"/> = expr_evaluate(g_superspectate)) + registration.
///   - The ASF_* (autospec) and SSF_* (superspec) flag bit constants and ASF_ALL, bit-identical to Base 8-25.
///   - Per-client option state (autospec_flags / superspec_flags / superspec_itemfilter), held in a side-table
///     keyed by <see cref="Player"/> (the port's <see cref="Player"/> has no superspec fields and this file may
///     not edit Player.cs — see todos to promote these to real .fields for networking/persistence).
///   - The item-message classname filter (<see cref="FilterItem"/>, superspec_filteritem: empty = match all).
///   - The Spectate transfer helper (<see cref="SpectateTransfer"/>) built on the port's <see cref="Player.Spectatee"/>
///     / <see cref="Player.SpectateeStatus"/> follow-cam state (the port has no engine Spectate() primitive yet).
///   - The PlayerDies followkiller hook (ASF_FOLLOWKILLER): a spectator following the victim auto-switches to
///     the killer. Wired via the live <see cref="MutatorHooks.PlayerDies"/> chain.
///   - The SV_ParseClientCommand spectator verbs (<c>superspec</c>/<c>autospec</c>/<c>superspec_itemfilter</c>/
///     <c>followpowerup</c>/<c>followstrength</c>/<c>followshield</c>), via <see cref="HandleCommand"/> — routed
///     on the live path by the server command bus (Commands.CmdSuperspec, the same pattern as the sandbox verb).
///     This is what WRITES the per-client option store, so the followkiller hook + FilterItem + Msg are now
///     reachable in a real match. Console output goes through the host-wired <see cref="PrintTo"/> sprint seam.
///
/// ALSO PORTED (Wave-5 fix-up):
///   - The ItemTouch hook (<see cref="OnItemTouch"/>, via the new <c>MutatorHooks.ItemTouch</c> chain fired from
///     <c>ItemPickupRules.ItemTouch</c>): the (filtered) pickup-announcement messages + auto-spectate on an item
///     message (ASF_SSIM) or a Strength/Shield/Mega-Armor/Mega-Health/flag-grab grab, with observer_only suppression.
///     This is the live caller for <see cref="FilterItem"/>, <see cref="Msg"/>, and <see cref="SpectateTransfer"/>.
///   - The <c>superspec_msg</c> centered form: <see cref="Msg"/> now routes the gated centerprint through the
///     notification CenterRaw channel (no host seam needed).
///   - ClientConnect/ClientDisconnect (<see cref="OnClientConnect"/>/<see cref="OnClientDisconnect"/>, called by the
///     server's client lifecycle): the connect defaults, the crypto_idfp-keyed options-file load/save (magic
///     "SUPERSPEC_OPTIONSFILE_V1", via the host-wired <see cref="Store"/>), and the delayed superspec_hello firing
///     INFO_SUPERSPEC_MISSING_UID (scheduled at time+5, driven off the SvStartFrame chain).
///
/// CROSS-FILE SEAMS THAT DO NOT EXIST YET (left as todos, NOT ported here):
///   - A robust IS_LOCAL (listen-host) discriminator: <see cref="IsLocalProvider"/> is left unset by the host, so
///     the options file is crypto_idfp-keyed for everyone (a dedicated-server behaviour). An authenticated client
///     persists; an unauthenticated remote client (and a listen host's local-file fast path) does not.
/// </summary>
[Mutator]
public sealed class SuperSpecMutator : MutatorBase
{
    // QC autospec_flags bits (sv_superspec.qc:8-17).
    public const int ASF_STRENGTH = 1 << 0;
    public const int ASF_SHIELD = 1 << 1;
    public const int ASF_MEGA_AR = 1 << 2;
    public const int ASF_MEGA_HP = 1 << 3;
    public const int ASF_FLAG_GRAB = 1 << 4;
    public const int ASF_OBSERVER_ONLY = 1 << 5;
    public const int ASF_SHOWWHAT = 1 << 6;
    public const int ASF_SSIM = 1 << 7;
    public const int ASF_FOLLOWKILLER = 1 << 8;
    public const int ASF_ALL = 0xFFFFFF;

    // QC superspec_flags bits (sv_superspec.qc:20-22).
    public const int SSF_SILENT = 1 << 0;
    public const int SSF_VERBOSE = 1 << 1;
    public const int SSF_ITEMMSG = 1 << 2;

    /// <summary>QC <c>_SSMAGIX</c> options-file magic (used by the not-yet-ported persistence path).</summary>
    public const string OptionsFileMagic = "SUPERSPEC_OPTIONSFILE_V1";

    public SuperSpecMutator() => NetName = "superspec";

    // ===== host seams (wired by the server; null in tests / headless) =====

    /// <summary>QC <c>sprint(to, ..)</c> — one console line to a spectator (the host routes it to the client).
    /// Wired by the server (NetGame.WireSuperspec). When null (tests/headless) the sprint line is dropped; the
    /// centered <c>centerprint</c> form goes through the notification CenterRaw channel directly (no host seam).</summary>
    public Action<Player, string>? PrintTo { get; set; }

    /// <summary>QC <c>FOREACH_CLIENT</c> — the live client roster. The follow* commands and the followkiller scan
    /// pull from this; the host wires it (<c>() =&gt; Clients.Players</c>). Falls back to the option-store keys
    /// (tests) so the unit stays exercisable headless.</summary>
    public Func<IReadOnlyList<Player>>? RosterProvider { get; set; }

    /// <summary>
    /// QC the per-client options file IO (<c>fopen</c>/<c>fputs</c>/<c>fgets</c> in superspec_save_client_conf /
    /// ClientConnect, sv_superspec.qc:33-61/378-419). Persists the per-client flags + itemfilter to
    /// <c>superspec-local.options</c> (listen host) or <c>superspec-&lt;uri_escape(crypto_idfp)&gt;.options</c>
    /// (an authenticated remote client). The host wires a file-backed store (NetGame); null (tests/headless) =
    /// no persistence, exactly as a dedicated server with no writable conf dir. Read returns the file's lines
    /// (or null on miss); Write replaces them.
    /// </summary>
    public IOptionsStore? Store { get; set; }

    /// <summary>QC <c>.crypto_idfp</c> — the player's authentication UID (same field RaceRecords/Sandbox key by;
    /// the port's <see cref="Player.PersistentId"/>). "" = unauthenticated (no remote persistence; a missing-UID
    /// notification fires). The host wires it; the fallback reads <see cref="Player.PersistentId"/> directly.</summary>
    public Func<Player, string>? CryptoIdfpProvider { get; set; }

    /// <summary>QC <c>IS_LOCAL(player)</c> — is this the listen-server host (who persists to the fixed local file
    /// regardless of UID)? The host wires it; the fallback treats nobody as local (dedicated-server behaviour).</summary>
    public Func<Player, bool>? IsLocalProvider { get; set; }

    private string CryptoIdfp(Player p) => CryptoIdfpProvider is not null ? CryptoIdfpProvider(p) : p.PersistentId;
    private bool IsLocal(Player p) => IsLocalProvider is not null && IsLocalProvider(p);

    /// <summary>The per-client options-file persistence seam (QC fopen/fputs/fgets). Implemented by the host over
    /// the writable user dir; null in tests/headless.</summary>
    public interface IOptionsStore
    {
        /// <summary>QC <c>fopen(fn, FILE_READ)</c> + <c>fgets</c> loop: the file's lines, or null if absent.</summary>
        IReadOnlyList<string>? Read(string filename);
        /// <summary>QC <c>fopen(fn, FILE_WRITE)</c> + <c>fputs</c>: replace the file with these lines.</summary>
        void Write(string filename, IReadOnlyList<string> lines);
    }

    private IReadOnlyList<Player> Roster()
    {
        if (RosterProvider is not null) return RosterProvider();
        var list = new List<Player>(_options.Count);
        foreach (Player p in _options.Keys) list.Add(p);
        return list;
    }

    // QC: REGISTER_MUTATOR(superspec, expr_evaluate(autocvar_g_superspectate)). g_superspectate is a STRING.
    public override bool IsEnabled =>
        Api.Services is not null && ExprEvaluate(Api.Cvars.GetString("g_superspectate"));

    // ===== per-client option state (QC .autospec_flags / .superspec_flags / .superspec_itemfilter) =====
    // Held off-Player because this file may not edit Player.cs. SSF_VERBOSE is the QC ClientConnect default
    // (sv_superspec.qc:387); itemfilter defaults to "" (match-all). See todos: promote to real Player fields.
    private sealed class Options
    {
        public int AutospecFlags;
        public int SuperspecFlags = SSF_VERBOSE;
        public string ItemFilter = "";
    }

    private static readonly Dictionary<Player, Options> _options = new();

    // QC superspec_delayed_hello: the (player, fire-time) of a scheduled missing-UID notification (think at
    // time + 5). Drained by OnStartFrame once the fire-time passes — the port has no per-entity think scheduler
    // reachable from the mutator, so the SvStartFrame chain stands in for the new_pure(...).nextthink entity.
    private static readonly List<(Player Player, float When)> _pendingHello = new();

    private static Options Opt(Player p)
    {
        if (!_options.TryGetValue(p, out Options? o))
        {
            o = new Options();
            _options[p] = o;
        }
        return o;
    }

    private HookHandler<MutatorHooks.PlayerDiesArgs>? _onPlayerDies;
    private HookHandler<MutatorHooks.ItemTouchArgs>? _onItemTouch;
    private HookHandler<MutatorHooks.SvStartFrameArgs>? _onStartFrame;

    public override void Hook()
    {
        _onPlayerDies ??= OnPlayerDies;
        _onItemTouch ??= OnItemTouch;
        _onStartFrame ??= OnStartFrame;
        MutatorHooks.PlayerDies.Add(_onPlayerDies);
        MutatorHooks.ItemTouch.Add(_onItemTouch);
        MutatorHooks.SvStartFrame.Add(_onStartFrame);
    }

    public override void Unhook()
    {
        if (_onPlayerDies is not null) MutatorHooks.PlayerDies.Remove(_onPlayerDies);
        if (_onItemTouch is not null) MutatorHooks.ItemTouch.Remove(_onItemTouch);
        if (_onStartFrame is not null) MutatorHooks.SvStartFrame.Remove(_onStartFrame);
        _options.Clear();
        _pendingHello.Clear();
    }

    // QC superspec_Spectate(this, targ) -> Spectate(this, targ). The port has no engine Spectate() target-switch
    // primitive, so this sets the follow-cam state directly (.enemy = targ, spectatee_status = targ's id), which
    // is the faithful observable result. Returns true like Base.
    private static bool SpectateTransfer(Player spectator, Player target)
    {
        spectator.Spectatee = target;
        spectator.SpectateeStatus = target.PlayerId;
        return true;
    }

    // QC superspec_filteritem(_for, _item): empty filter = match all; else tokenized classname allowlist.
    public static bool FilterItem(Player forPlayer, Entity item)
    {
        string filter = Opt(forPlayer).ItemFilter;
        if (filter.Length == 0)
            return true;

        foreach (string token in filter.Split((char[]?)null, System.StringSplitOptions.RemoveEmptyEntries))
        {
            if (token == item.ClassName)
                return true;
        }
        return false;
    }

    // QC superspec_msg(center_title, con_title, to, msg, spamlevel): always console-print; centerprint unless
    // SSF_SILENT, or (spamlevel > 1 && !SSF_VERBOSE). The console (sprint) form is now wired through PrintTo; the
    // centered (centerprint) form has no server seam yet, so its gating is resolved faithfully but the text is
    // still discarded (see todos).
    public void Msg(Player to, string centerTitle, string conTitle, string msg, float spamLevel)
    {
        int flags = Opt(to).SuperspecFlags;

        // sprint(_to, strcat(conTitle, msg)) — always.
        PrintTo?.Invoke(to, conTitle + msg);

        if ((flags & SSF_SILENT) != 0)
            return;

        if (spamLevel > 1f && (flags & SSF_VERBOSE) == 0)
            return;

        // centerprint(_to, strcat(centerTitle, msg)) — gated. Routed through the notification CenterRaw channel
        // (the same MSG_CenterRaw path Chat.cs / map triggers use for a raw centerprint to one client), so the
        // spectator gets the centered overlay too, not just the console line. NOTIF_ONE_ONLY targets exactly the
        // recipient. Headless/tests reach the RecordingSink, so the unit stays observable without the net layer.
        NotificationSystem.SendCenterRaw(NotifBroadcast.OneOnly, to, centerTitle + msg);
    }

    // MUTATOR_HOOKFUNCTION(superspec, PlayerDies) — followkiller (sv_superspec.qc:421-435). Any spectator
    // following the victim auto-switches to the killer when ASF_FOLLOWKILLER is set and the killer is a player.
    private bool OnPlayerDies(ref MutatorHooks.PlayerDiesArgs args)
    {
        if (args.Attacker is not Player attacker || !IsLivePlayer(attacker))
            return false;
        if (args.Target is not Player victim)
            return false;

        foreach (Player it in Roster())
        {
            if (!IsSpectator(it))
                continue;
            Options o = Opt(it);
            if ((o.AutospecFlags & ASF_FOLLOWKILLER) != 0 && ReferenceEquals(it.Spectatee, victim))
            {
                if ((o.AutospecFlags & ASF_SHOWWHAT) != 0)
                    Msg(it, "", "", $"^7Following {attacker.NetName}^7 due to followkiller\n", 2);

                SpectateTransfer(it, attacker);
            }
        }
        return false;
    }

    // MUTATOR_HOOKFUNCTION(superspec, ItemTouch) — sv_superspec.qc:91-139. Fired (via MutatorHooks.ItemTouch /
    // ItemPickupRules.ItemTouch) when a player is about to collect a world item, BEFORE the give. For every
    // spectator/observer: optionally print a (filtered) pickup message, and auto-switch the follow-cam onto the
    // toucher on an item-message (ASF_SSIM) or on a Strength/Shield/Mega-Armor/Mega-Health/flag-grab trigger,
    // honouring the observer_only suppression. QC returns MUT_ITEMTOUCH_CONTINUE (never blocks the pickup).
    private bool OnItemTouch(ref MutatorHooks.ItemTouchArgs args)
    {
        if (args.Item is not { } item || args.Toucher is not Player toucher)
            return false;

        // QC item.netname is the item's human label (def.m_name). The port's world item carries the bare
        // NetName (= def.NetName) for net keying, so reach the def's DisplayName for the message; fall back to
        // NetName when there's no def. (item.classname stays the raw classname, faithful to QC.)
        string itemLabel = !string.IsNullOrEmpty(item.Pickup?.DisplayName) ? item.Pickup!.DisplayName : item.NetName;

        foreach (Player it in Roster())
        {
            // QC: if(!IS_SPEC(it) && !IS_OBSERVER(it)) continue; — only spectators/observers react.
            if (!it.IsObserver)
                continue;

            Options o = Opt(it);

            // ---- item-message (SSF_ITEMMSG) + the ASF_SSIM auto-follow ----
            if ((o.SuperspecFlags & SSF_ITEMMSG) != 0 && FilterItem(it, item))
            {
                if ((o.SuperspecFlags & SSF_VERBOSE) != 0)
                    Msg(it, "", "", $"Player {toucher.NetName}^7 just picked up ^3{itemLabel}\n", 1);
                else
                    Msg(it, "", "", $"Player {toucher.NetName}^7 just picked up ^3{itemLabel}\n^8({item.ClassName}^8)\n", 1);

                if ((o.AutospecFlags & ASF_SSIM) != 0 && !ReferenceEquals(it.Spectatee, toucher))
                {
                    SpectateTransfer(it, toucher);
                    continue; // QC: return MUT_ITEMTOUCH_CONTINUE (stops processing this spectator's other triggers)
                }
            }

            // ---- the powerup / mega / flag auto-follow triggers ----
            bool trigger =
                (((o.AutospecFlags & ASF_SHIELD) != 0) && item.InvincibleFinished != 0f)
                || (((o.AutospecFlags & ASF_STRENGTH) != 0) && item.StrengthFinished != 0f)
                || (((o.AutospecFlags & ASF_MEGA_AR) != 0) && item.Pickup is ArmorMega)
                || (((o.AutospecFlags & ASF_MEGA_HP) != 0) && item.Pickup is HealthMega)
                || (((o.AutospecFlags & ASF_FLAG_GRAB) != 0) && item.ClassName == "item_flag_team");

            // QC: if((it.enemy != toucher) || IS_OBSERVER(it)). In this port IS_OBSERVER (a pure, non-following
            // observer) is IsObserver && Spectatee == null, so this gate reduces to "not already following the
            // toucher" (a pure observer also satisfies Spectatee != toucher).
            if (trigger && !ReferenceEquals(it.Spectatee, toucher))
            {
                // QC: ASF_OBSERVER_ONLY suppresses the switch unless the spectator is a true (non-following)
                // observer — i.e. suppress for a FOLLOWING spectator (port: IsObserver && Spectatee != null).
                if ((o.AutospecFlags & ASF_OBSERVER_ONLY) != 0 && it.Spectatee is not null)
                {
                    if ((o.SuperspecFlags & SSF_VERBOSE) != 0)
                        Msg(it, "", "", $"^8Ignored that ^7{toucher.NetName}^8 grabbed {itemLabel}^8 since the observer_only option is ON\n", 2);
                }
                else
                {
                    if ((o.AutospecFlags & ASF_SHOWWHAT) != 0)
                        Msg(it, "", "", $"^7Following {toucher.NetName}^7 due to picking up {itemLabel}\n", 2);

                    SpectateTransfer(it, toucher);
                }
            }
        }

        return false; // MUT_ITEMTOUCH_CONTINUE — never blocks the pickup.
    }

    // QC IS_SPEC(it): an observer that is currently following a live player (.enemy set, spectatee_status > 0).
    private static bool IsSpectator(Player p) => p.IsObserver && p.Spectatee is not null;

    // QC IS_PLAYER(e): a live, joined, non-observer client.
    private static bool IsLivePlayer(Player p) => !p.IsObserver && (p.Flags & EntFlags.Client) != 0;

    /// <summary>QC <c>StatusEffects_active(STATUSEFFECT_Strength, it)</c> — has the Strength powerup active.</summary>
    private static bool HasStrength(Player p)
        => StatusEffectsCatalog.ByName("strength") is { } d && StatusEffectsCatalog.Has(p, d);

    /// <summary>QC <c>StatusEffects_active(STATUSEFFECT_Shield, it)</c> — has the Shield (invincible) powerup active.</summary>
    private static bool HasShield(Player p)
        => StatusEffectsCatalog.ByName("shield") is { } d && StatusEffectsCatalog.Has(p, d);

    // =================================================================================================
    //  Per-client lifecycle + options-file persistence (sv_superspec.qc:33-61, 370-419, 437-442).
    //  Called by the server from its ClientConnect/ClientDisconnect chain (GameWorld.Infra*), gated on the
    //  mutator being Added — the C# stand-in for the MUTATOR_HOOKFUNCTION(superspec, ClientConnect/Disconnect).
    // =================================================================================================

    private const string LocalOptionsFile = "superspec-local.options";

    /// <summary>QC the superspec ClientConnect hook (sv_superspec.qc:378-419): seed the connect defaults
    /// (superspec_flags = SSF_VERBOSE, itemfilter = ""), schedule the time+5 missing-UID hello, then load the
    /// per-client options file (local for the listen host, crypto_idfp-keyed for an authenticated remote).</summary>
    public void OnClientConnect(Player player)
    {
        // QC: if(!IS_REAL_CLIENT(player)) return; — bots/console never persist.
        if (player.IsBot)
            return;

        // QC connect defaults (the options-file load overrides these if present).
        Options opt = Opt(player);
        opt.SuperspecFlags = SSF_VERBOSE;
        opt.ItemFilter = "";

        // QC: schedule superspec_hello at time + 5 (fires INFO_SUPERSPEC_MISSING_UID if the client has no UID).
        _pendingHello.Add((player, (Api.Services is not null ? Api.Clock.Time : 0f) + 5f));

        // QC: load superspec-local.options (host) or superspec-<uri_escape(crypto_idfp)>.options (remote w/ UID).
        if (Store is null)
            return;

        string fn = LocalOptionsFile;
        if (!IsLocal(player))
        {
            string idfp = CryptoIdfp(player);
            if (string.IsNullOrEmpty(idfp))
                return; // QC: no UID → cannot key a remote file → keep the connect defaults.
            fn = $"superspec-{UriEscape(idfp)}.options";
        }

        IReadOnlyList<string>? lines = Store.Read(fn);
        if (lines is null || lines.Count == 0)
            return;

        // QC: line0 = magic, line1 = autospec_flags, line2 = superspec_flags, line3 = itemfilter.
        if (lines[0] != OptionsFileMagic)
            return; // QC LOG_TRACE("unknown magic") and bail.
        if (lines.Count > 1 && int.TryParse(lines[1], out int af)) opt.AutospecFlags = af;
        if (lines.Count > 2 && int.TryParse(lines[2], out int sf)) opt.SuperspecFlags = sf;
        if (lines.Count > 3) opt.ItemFilter = lines[3];
    }

    /// <summary>QC the superspec ClientDisconnect hook (sv_superspec.qc:437-442) → superspec_save_client_conf:
    /// write the per-client flags + itemfilter back to the options file (local host or crypto_idfp-keyed remote;
    /// an unauthenticated remote client is not saved).</summary>
    public void OnClientDisconnect(Player player)
    {
        SaveClientConf(player);
        _pendingHello.RemoveAll(h => ReferenceEquals(h.Player, player));
        _options.Remove(player);
    }

    // QC superspec_save_client_conf(this) (sv_superspec.qc:33-61).
    private void SaveClientConf(Player player)
    {
        if (Store is null || player.IsBot)
            return;

        string fn = LocalOptionsFile;
        if (!IsLocal(player))
        {
            string idfp = CryptoIdfp(player);
            if (string.IsNullOrEmpty(idfp))
                return; // QC: no UID → don't save a remote client.
            fn = $"superspec-{UriEscape(idfp)}.options";
        }

        Options opt = Opt(player);
        Store.Write(fn, new[]
        {
            OptionsFileMagic,
            opt.AutospecFlags.ToString(System.Globalization.CultureInfo.InvariantCulture),
            opt.SuperspecFlags.ToString(System.Globalization.CultureInfo.InvariantCulture),
            opt.ItemFilter,
        });
    }

    // QC superspec_hello (sv_superspec.qc:370-376) driven off the SvStartFrame chain: once the scheduled
    // time+5 passes, if the client still lacks a UID, fire INFO_SUPERSPEC_MISSING_UID to that client only.
    private bool OnStartFrame(ref MutatorHooks.SvStartFrameArgs args)
    {
        if (_pendingHello.Count == 0)
            return false;

        float now = args.Time;
        for (int i = _pendingHello.Count - 1; i >= 0; --i)
        {
            if (now < _pendingHello[i].When)
                continue;
            Player p = _pendingHello[i].Player;
            _pendingHello.RemoveAt(i);
            if (string.IsNullOrEmpty(CryptoIdfp(p)))
                NotificationSystem.Send(NotifBroadcast.OneOnly, p, MsgType.Info, "SUPERSPEC_MISSING_UID");
        }
        return false;
    }

    // QC uri_escape(s): percent-escape the bytes a filename can't safely carry. crypto_idfp is base64-ish (it can
    // contain '/' and '+'), so escaping those keeps the filename single-segment + filesystem-safe, matching the
    // intent of Base's uri_escape on the remote-options filename.
    private static string UriEscape(string s)
    {
        var sb = new StringBuilder(s.Length);
        foreach (char c in s)
        {
            if ((c >= 'A' && c <= 'Z') || (c >= 'a' && c <= 'z') || (c >= '0' && c <= '9')
                || c == '-' || c == '_' || c == '.' || c == '~')
                sb.Append(c);
            else
                sb.Append('%').Append(((int)c).ToString("X2", System.Globalization.CultureInfo.InvariantCulture));
        }
        return sb.ToString();
    }

    // =================================================================================================
    // MUTATOR_HOOKFUNCTION(superspec, SV_ParseClientCommand) — sv_superspec.qc:141-358. The spectator-only
    // verbs superspec_itemfilter / superspec / autospec / followpowerup / followstrength / followshield. The
    // server command bus (Commands.CmdSuperspec) routes a client `cmd <verb> …` here when the mutator is added,
    // mirroring the sandbox g_sandbox routing. argv[0] is the verb; argv[1..] the options. Returns true when the
    // command was consumed (QC `return true`). QC's IS_PLAYER(player) early-return makes these spectator-only.
    // =================================================================================================
    public bool HandleCommand(Player player, string[] argv)
    {
        if (argv is null || argv.Length == 0)
            return false;

        // QC: if(IS_PLAYER(player)) return;  — these are spectator/observer-only commands.
        if (IsLivePlayer(player))
            return false;

        string cmdName = argv[0];
        string Argv(int i) => i >= 0 && i < argv.Length ? argv[i] : "";
        int argc = argv.Length;
        Options opt = Opt(player);

        if (cmdName == "superspec_itemfilter")
        {
            if (Argv(1) == "help")
            {
                Msg(player, "^3superspec_itemfilter help:\n\n\n", "\n^3superspec_itemfilter help:\n",
                    "^7 superspec_itemfilter ^3\"item_classname1 item_classname2\"^7 only show thise items when ^2superspec ^3item_message^7 is on\n"
                    + "^3 clear^7 Remove the filter (show all pickups)\n"
                    + "^3 show ^7 Display current filter\n", 1);
            }
            else if (Argv(1) == "clear")
            {
                opt.ItemFilter = "";
            }
            else if (Argv(1) == "show" || Argv(1) == "")
            {
                if (opt.ItemFilter.Length == 0)
                {
                    Msg(player, "^3superspec_itemfilter^7 is ^1not^7 set", "\n^3superspec_itemfilter^7 is ^1not^7 set\n", "", 1);
                    return true;
                }
                string[] toks = opt.ItemFilter.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
                var sb = new StringBuilder();
                for (int i = 0; i < toks.Length; ++i)
                    sb.Append("^3#").Append(i).Append(" ^7").Append(toks[i]).Append('\n');
                sb.Append('\n');
                Msg(player, "^3superspec_itemfilter is:\n\n\n", "\n^3superspec_itemfilter is:\n", sb.ToString(), 1);
            }
            else
            {
                opt.ItemFilter = Argv(1);
            }
            return true;
        }

        if (cmdName == "superspec")
        {
            if (argc > 1)
            {
                if (Argv(1) == "help")
                {
                    Msg(player, "^2Available Super Spectate ^3options:\n\n\n", "\n^2Available Super Spectate ^3options:\n",
                        "use cmd superspec [option] [on|off] to set options\n\n"
                        + "^3 silent ^7(short^5 si^7) supresses ALL messages from superspectate.\n"
                        + "^3 verbose ^7(short^5 ve^7) makes superspectate print some additional information.\n"
                        + "^3 item_message ^7(short^5 im^7) makes superspectate print items that were picked up.\n"
                        + "^7    Use cmd superspec_itemfilter \"item_class1 item_class2\" to set up a filter of what to show with ^3item_message.\n", 1);
                    return true;
                }

                int bits = 0, start = 1;
                if (Argv(1) == "clear")
                {
                    opt.SuperspecFlags = 0;
                    start = 2;
                }

                for (int i = start; i < argc; ++i)
                {
                    string s = Argv(i);
                    if (s == "on" || s == "1")
                    {
                        opt.SuperspecFlags |= bits;
                        bits = 0;
                    }
                    else if (s == "off" || s == "0")
                    {
                        if (start == 1)
                            opt.SuperspecFlags &= ~bits;
                        bits = 0;
                    }
                    else
                    {
                        if (s == "silent" || s == "si") bits |= SSF_SILENT;
                        if (s == "verbose" || s == "ve") bits |= SSF_VERBOSE;
                        if (s == "item_message" || s == "im") bits |= SSF_ITEMMSG;
                    }
                }
            }

            var info = new StringBuilder();
            OptionInfo(info, opt.SuperspecFlags, SSF_SILENT, "Silent", "silent", "si");
            OptionInfo(info, opt.SuperspecFlags, SSF_VERBOSE, "Verbose", "verbose", "ve");
            OptionInfo(info, opt.SuperspecFlags, SSF_ITEMMSG, "Item pickup messages", "item_message", "im");
            Msg(player, "^3Current Super Spectate options are:\n\n\n\n\n", "\n^3Current Super Spectate options are:\n", info.ToString(), 1);
            return true;
        }

        if (cmdName == "autospec")
        {
            if (argc > 1)
            {
                if (Argv(1) == "help")
                {
                    Msg(player, "^2Available Auto Spectate ^3options:\n\n\n", "\n^2Available Auto Spectate ^3options:\n",
                        "use cmd autospec [option] [on|off] to set options\n\n"
                        + "^3 strength ^7(short^5 st^7) for automatic spectate on strength powerup\n"
                        + "^3 shield ^7(short^5 sh^7) for automatic spectate on shield powerup\n"
                        + "^3 mega_health ^7(short^5 mh^7) for automatic spectate on mega health\n"
                        + "^3 mega_armor ^7(short^5 ma^7) for automatic spectate on mega armor\n"
                        + "^3 flag_grab ^7(short^5 fg^7) for automatic spectate on CTF flag grab\n"
                        + "^3 observer_only ^7(short^5 oo^7) for automatic spectate only if in observer mode\n"
                        + "^3 show_what ^7(short^5 sw^7) to display what event triggered autospectate\n"
                        + "^3 item_msg ^7(short^5 im^7) to autospec when item_message in superspectate is triggered\n"
                        + "^3 followkiller ^7(short ^5fk^7) to autospec the killer/off\n"
                        + "^3 all ^7(short ^5aa^7) to turn everything on/off\n", 1);
                    return true;
                }

                int bits = 0, start = 1;
                if (Argv(1) == "clear")
                {
                    opt.AutospecFlags = 0;
                    start = 2;
                }

                for (int i = start; i < argc; ++i)
                {
                    string s = Argv(i);
                    if (s == "on" || s == "1")
                    {
                        opt.AutospecFlags |= bits;
                        bits = 0;
                    }
                    else if (s == "off" || s == "0")
                    {
                        if (start == 1)
                            opt.AutospecFlags &= ~bits;
                        bits = 0;
                    }
                    else
                    {
                        if (s == "strength" || s == "st") bits |= ASF_STRENGTH;
                        if (s == "shield" || s == "sh") bits |= ASF_SHIELD;
                        if (s == "mega_health" || s == "mh") bits |= ASF_MEGA_HP;
                        if (s == "mega_armor" || s == "ma") bits |= ASF_MEGA_AR;
                        if (s == "flag_grab" || s == "fg") bits |= ASF_FLAG_GRAB;
                        if (s == "observer_only" || s == "oo") bits |= ASF_OBSERVER_ONLY;
                        if (s == "show_what" || s == "sw") bits |= ASF_SHOWWHAT;
                        if (s == "item_msg" || s == "im") bits |= ASF_SSIM;
                        if (s == "followkiller" || s == "fk") bits |= ASF_FOLLOWKILLER;
                        if (s == "all" || s == "aa") bits |= ASF_ALL;
                    }
                }
            }

            var info = new StringBuilder();
            OptionInfo(info, opt.AutospecFlags, ASF_STRENGTH, "Strength", "strength", "st");
            OptionInfo(info, opt.AutospecFlags, ASF_SHIELD, "Shield", "shield", "sh");
            OptionInfo(info, opt.AutospecFlags, ASF_MEGA_HP, "Mega Health", "mega_health", "mh");
            OptionInfo(info, opt.AutospecFlags, ASF_MEGA_AR, "Mega Armor", "mega_armor", "ma");
            OptionInfo(info, opt.AutospecFlags, ASF_FLAG_GRAB, "Flag grab", "flag_grab", "fg");
            OptionInfo(info, opt.AutospecFlags, ASF_OBSERVER_ONLY, "Only switch if observer", "observer_only", "oo");
            OptionInfo(info, opt.AutospecFlags, ASF_SHOWWHAT, "Show what item triggered spectate", "show_what", "sw");
            OptionInfo(info, opt.AutospecFlags, ASF_SSIM, "Switch on superspec item message", "item_msg", "im");
            OptionInfo(info, opt.AutospecFlags, ASF_FOLLOWKILLER, "Followkiller", "followkiller", "fk");
            Msg(player, "^3Current auto spectate options are:\n\n\n\n\n", "\n^3Current auto spectate options are:\n", info.ToString(), 1);
            return true;
        }

        if (cmdName == "followpowerup")
        {
            foreach (Player it in Roster())
                if (IsLivePlayer(it) && (HasStrength(it) || HasShield(it)))
                    return SpectateTransfer(player, it);
            Msg(player, "", "", "No active powerup\n", 1);
            return true;
        }

        if (cmdName == "followstrength")
        {
            foreach (Player it in Roster())
                if (IsLivePlayer(it) && HasStrength(it))
                    return SpectateTransfer(player, it);
            Msg(player, "", "", "No active Strength\n", 1);
            return true;
        }

        if (cmdName == "followshield")
        {
            foreach (Player it in Roster())
                if (IsLivePlayer(it) && HasShield(it))
                    return SpectateTransfer(player, it);
            Msg(player, "", "", "No active Shield\n", 1);
            return true;
        }

        return false;
    }

    // MUTATOR_HOOKFUNCTION(superspec, BuildMutatorsString) — sv_superspec.qc:360-363.
    public override string BuildMutatorsString(string s) => s + ":SS";

    // MUTATOR_HOOKFUNCTION(superspec, BuildMutatorsPrettyString) — sv_superspec.qc:365-368.
    public override string BuildMutatorsPrettyString(string s) => s + ", Super Spectators";

    // QC OPTIONINFO(flag, msg, test, text, long, short): "[ON]/[OFF] text (^3 long | ^3 short )\n".
    private static void OptionInfo(StringBuilder sb, int flag, int test, string text, string longName, string shortName)
        => sb.Append((flag & test) != 0 ? "^2[ON]  ^7" : "^1[OFF] ^7")
             .Append(text).Append(" ^7(^3 ").Append(longName).Append("^7 | ^3").Append(shortName).Append(" ^7)\n");

    /// <summary>QC <c>expr_evaluate(s)</c> for a cvar string: false for "" / "0" / "false", true otherwise.</summary>
    private static bool ExprEvaluate(string s)
    {
        if (string.IsNullOrEmpty(s)) return false;
        s = s.Trim();
        if (s == "0" || string.Equals(s, "false", System.StringComparison.OrdinalIgnoreCase)) return false;
        return true;
    }
}
