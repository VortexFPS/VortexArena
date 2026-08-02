namespace VortexArena.Net;

/// <summary>
/// What this game calls itself on the wire, in one place.
///
/// <para>dpmaster keys its server lists off the <c>gamename</c> a server reports in its <c>infoResponse</c>
/// (the heartbeat itself is game-independent), and a client asks for one game name per <c>getservers</c>
/// query. So this single constant decides both which servers a host is listed alongside and which servers a
/// browser gets back.</para>
///
/// <para>Vortex Arena advertises itself as <see cref="Name"/>: its netcode is a ground-up reimplementation
/// that shares no wire format with Darkplaces, so a host of this game listed among Xonotic's would be an
/// entry no Xonotic client can join and no Vortex client can distinguish. <see cref="LegacyName"/> is kept
/// separately because the browser deliberately keeps querying for Xonotic servers too — they are listed (and
/// then refused at Join with an explanation) until backwards-compatible support exists.</para>
/// </summary>
public static class GameIdentity
{
    /// <summary>This game's dpmaster game name — what a VortexArena server reports and is listed under.</summary>
    public const string Name = "Vortex";

    /// <summary>
    /// The upstream game whose servers the browser also lists. Queried alongside <see cref="Name"/> against
    /// every configured master, so the list stays populated while Vortex's own master (Conductor/VortexFPS)
    /// and its server population are still being stood up.
    /// </summary>
    public const string LegacyName = "Xonotic";

    /// <summary>The Darkplaces network protocol version the masters index by (DP <c>NET_PROTOCOL_VERSION</c>).</summary>
    public const int DpProtocol = 3;
}
