using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using VortexArena.Common.Diagnostics;

namespace VortexArena.Server;

/// <summary>The dedicated world's Matrix client. Commands and Pump run on the server owner thread.</summary>
internal sealed class MatrixChatBridge(Func<string> serverSetting, Action<string> receive) : IDisposable
{
    internal const string DefaultServer = "http://matrix.org";
    private const string ApiPath = "/_matrix/client/r0";
    private const int QueueLimit = 64;

    private enum PollMode { Off, Sync, Messages }
    private enum WriteKind { Nick, Typing, Say, Join, Leave, Forget }
    private readonly record struct Write(WriteKind Kind, string Value);
    private readonly record struct Response(int Status, JsonElement Payload)
    {
        public bool Success => Status is >= 200 and < 300;
    }

    private readonly Queue<Write> _writes = new();
    private HttpClient? _http;
    private Uri? _server;
    private string _serverValue = "";
    private string _user = "";
    private string _token = "";
    private string _requestedRoom = "";
    private string _joinedRoom = "";
    private string _joinedRoomId = "";
    private bool _forgetOnDeparture;
    private string _forgetRoomId = "";
    private string _rejectedJoin = "";
    private CancellationTokenSource? _writeCancellation;
    private CancellationTokenSource? _pollCancellation;
    private Task<Response>? _writeTask;
    private Task<Response>? _pollTask;
    private Write _writing;
    private Write? _retryWrite;
    private long _nextWrite;
    private PollMode _mode;
    private string _cursor = "";
    private long _nextPoll;
    private bool _reportedPollFailure;
    private readonly string _transactionPrefix = Guid.NewGuid().ToString("N");
    private long _transaction;

    public bool Execute(CommandContext ctx)
    {
        if (!ctx.IsServerConsole)
            return true;

        // Preserve spacing and embedded quotes in the text tail. A single quoted token supplies its contents.
        string value = ctx.RawTail(2);
        if (ctx.ArgCount == 3 && value.StartsWith('"') && value.EndsWith('"'))
            value = value[1..^1];
        RefreshServer();
        switch (ctx.Arg(1))
        {
            case "user":
                if (_user != value)
                    ResetPoll(_mode);
                _user = value;
                break;
            case "token":
                if (value.Length > 0 && (_server is null || value.Any(c => char.IsControl(c) || char.IsWhiteSpace(c))))
                {
                    ctx.Print("Matrix requires a valid token and an HTTPS homeserver or loopback HTTP.");
                    break;
                }
                if (_token == value)
                    break;
                ResetMembership();
                ResetPoll(PollMode.Off);
                ResetHttp();
                _token = value;
                break;
            case "join":
                if (value.Length < 2 || value[0] is not ('!' or '#'))
                    break;
                _rejectedJoin = "";
                if (_nextWrite == long.MaxValue)
                    _nextWrite = 0;
                if (value != _requestedRoom)
                {
                    DropRoomMessages();
                    ResetPoll(_mode);
                    _requestedRoom = value;
                }
                break;
            case "leave":
            case "forget":
                if (_nextWrite == long.MaxValue)
                    _nextWrite = 0;
                DropRoomMessages();
                ResetPoll(PollMode.Off);
                _requestedRoom = "";
                _forgetOnDeparture |= ctx.Arg(1) == "forget"
                    && (_joinedRoomId.Length > 0 || _writeTask is not null && _writing.Kind == WriteKind.Join
                        || _retryWrite?.Kind == WriteKind.Join);
                break;
            case "sync":
            case "messages":
                if (HasToken && _requestedRoom.Length > 0 && _user.Length > 0)
                    ResetPoll(ctx.Arg(1) == "sync" ? PollMode.Sync : PollMode.Messages);
                break;
            case "nick":
                if (HasToken && _user.Length > 0)
                    Enqueue(WriteKind.Nick, value);
                break;
            case "typing":
                if (HasToken && _requestedRoom.Length > 0 && _user.Length > 0)
                    Enqueue(WriteKind.Typing, "");
                break;
            case "say":
                SendChat(value);
                break;
        }
        return true;
    }

    public void SendChat(string body)
    {
        if (CanRelay && body.Length > 0)
            Enqueue(WriteKind.Say, body);
    }

    public bool CanRelay => HasToken && _requestedRoom.Length > 0;

    private bool HasToken => _token.Length > 0;
    private bool RoomReady => _joinedRoomId.Length > 0 && _joinedRoom == _requestedRoom
        && !_forgetOnDeparture && _retryWrite is null
        && !(_writeTask is not null && IsMembership(_writing.Kind));

    private static bool IsMembership(WriteKind kind) => kind is WriteKind.Join or WriteKind.Leave or WriteKind.Forget;

    private void Enqueue(WriteKind kind, string value)
    {
        if (_writes.Count < QueueLimit)
            _writes.Enqueue(new Write(kind, value));
    }

    private void RefreshServer()
    {
        string setting = serverSetting().Trim();
        if (_serverValue == setting)
            return;

        // Credentials are scoped to the configured homeserver. A cvar change requires a fresh token.
        _serverValue = setting;
        ResetMembership();
        ResetPoll(PollMode.Off);
        ResetHttp();
        _requestedRoom = _token = "";
        _server = null;
        if (Uri.TryCreate(setting, UriKind.Absolute, out Uri? uri)
            && (uri.Scheme == Uri.UriSchemeHttps || uri.Scheme == Uri.UriSchemeHttp && uri.IsLoopback)
            && uri.Host.Length > 0 && uri.UserInfo.Length == 0 && uri.Query.Length == 0 && uri.Fragment.Length == 0)
            _server = uri;
    }

    public void Pump()
    {
        if (!HasToken)
            return;
        using var scope = Prof.Sample("sv.matrix");
        RefreshServer();
        if (!HasToken)
            return;

        PumpWrites();

        if (_mode == PollMode.Off || !RoomReady)
            return;
        if (_pollTask is { IsCompleted: true } poll)
        {
            Response result = poll.GetAwaiter().GetResult();
            _pollTask = null;
            string? cursor = GetString(result.Payload, _mode == PollMode.Sync ? "next_batch" : "end");
            if (result.Success && !string.IsNullOrEmpty(cursor))
            {
                if (cursor != _cursor)
                    DeliverEvents(result.Payload);
                _cursor = cursor;
                _reportedPollFailure = false;
                _nextPoll = Environment.TickCount64 + 100;
            }
            else
            {
                if (!_reportedPollFailure)
                    ReportFailure(result, "poll");
                _reportedPollFailure = true;
                _nextPoll = Environment.TickCount64 + 1000;
            }
        }
        if (_pollTask is null && Environment.TickCount64 >= _nextPoll)
            _pollTask = StartPoll();
    }

    private void PumpWrites()
    {
        if (_writeTask is { IsCompleted: true } write)
        {
            Response result = write.GetAwaiter().GetResult();
            _writeTask = null;
            FinishWrite(result);
        }
        if (_writeTask is not null)
            return;

        // Reconcile membership before any message or poll. A join can have taken effect
        // before its response arrives, so keep the original request through a room change.
        Write next;
        if (_retryWrite is { } retry)
        {
            if (Environment.TickCount64 < _nextWrite)
                return;
            next = retry;
        }
        else if (_forgetRoomId.Length > 0)
            next = new(WriteKind.Forget, _forgetRoomId);
        else if (_joinedRoomId.Length > 0 && (_joinedRoom != _requestedRoom || _forgetOnDeparture))
            next = new(WriteKind.Leave, _joinedRoomId);
        else if (_requestedRoom.Length > 0 && _joinedRoomId.Length == 0 && _requestedRoom != _rejectedJoin)
            next = new(WriteKind.Join, _requestedRoom);
        else if (!_writes.TryDequeue(out next))
            return;

        if (next.Kind is WriteKind.Typing or WriteKind.Say && !RoomReady)
            return;
        _writing = next;
        _writeTask = StartWrite(next);
    }

    private void FinishWrite(Response result)
    {
        // A successful alias join must return its canonical room ID before it can be left.
        string? roomId = _writing.Kind == WriteKind.Join && result.Success
            ? GetString(result.Payload, "room_id") ?? (_writing.Value.StartsWith('!') ? _writing.Value : null)
            : null;
        bool joined = roomId is { Length: > 1 } && roomId[0] == '!';
        bool failed = !result.Success || _writing.Kind == WriteKind.Join && !joined;
        if (failed && _retryWrite is null)
            ReportFailure(result, "command");

        bool uncertain = result.Status is 0 or 408 or 429 or >= 500 || result.Success && failed;
        if (failed && (_writing.Kind == WriteKind.Leave || IsMembership(_writing.Kind) && uncertain))
        {
            _retryWrite = _writing;
            // Keep the confirmed membership on a failed leave. A definite refusal needs operator action.
            _nextWrite = uncertain ? Environment.TickCount64 + 1000 : long.MaxValue;
            return;
        }

        _retryWrite = null;
        switch (_writing.Kind)
        {
            case WriteKind.Join:
                if (joined)
                {
                    _joinedRoom = _writing.Value;
                    _joinedRoomId = roomId!;
                }
                else
                {
                    _rejectedJoin = _writing.Value;
                    _forgetOnDeparture = false;
                }
                break;
            case WriteKind.Leave:
                if (_forgetOnDeparture)
                    _forgetRoomId = _joinedRoomId;
                _forgetOnDeparture = false;
                _joinedRoom = _joinedRoomId = "";
                break;
            case WriteKind.Forget:
                _forgetRoomId = "";
                break;
        }
    }

    private Task<Response> StartWrite(Write write)
    {
        string roomPath = $"/rooms/{Escape(_joinedRoomId)}";
        string user = Escape(_user);
        _writeCancellation ??= new CancellationTokenSource();
        CancellationToken cancellation = _writeCancellation.Token;
        return write.Kind switch
        {
            WriteKind.Nick => Request(HttpMethod.Put, $"/profile/{user}/displayname",
                new { displayname = write.Value }, cancellation),
            WriteKind.Join => Request(HttpMethod.Post, write.Value.StartsWith('#') ? $"/join/{Escape(write.Value)}"
                : $"/rooms/{Escape(write.Value)}/join", new { }, cancellation),
            WriteKind.Leave => Request(HttpMethod.Post, $"/rooms/{Escape(write.Value)}/leave", new { }, cancellation),
            WriteKind.Forget => Request(HttpMethod.Post, $"/rooms/{Escape(write.Value)}/forget", new { }, cancellation),
            WriteKind.Typing => Request(HttpMethod.Put, $"{roomPath}/typing/{user}",
                new { typing = true, timeout = 30000 }, cancellation),
            WriteKind.Say => Request(HttpMethod.Put,
                $"{roomPath}/send/m.room.message/{_transactionPrefix}-{++_transaction}",
                new { msgtype = "m.text", body = write.Value }, cancellation),
            _ => throw new InvalidOperationException("Unknown Matrix operation.")
        };
    }

    private Task<Response> StartPoll()
    {
        _pollCancellation ??= new CancellationTokenSource();
        string path;
        if (_mode == PollMode.Messages)
            path = $"/events?room_id={Escape(_joinedRoomId)}&limit=50&timeout=30000&from={Escape(_cursor)}";
        else
        {
            bool initial = _cursor.Length == 0;
            var filter = new
            {
                account_data = new { types = Array.Empty<string>() },
                presence = new { types = Array.Empty<string>() },
                room = new { rooms = initial ? Array.Empty<string>() : new[] { _joinedRoomId } }
            };
            path = $"/sync?timeout={(initial ? 0 : 30000)}&filter={Escape(JsonSerializer.Serialize(filter))}";
            if (!initial)
                path += $"&since={Escape(_cursor)}";
        }
        return Request(HttpMethod.Get, path, null, _pollCancellation.Token);
    }

    private void DeliverEvents(JsonElement payload)
    {
        JsonElement events;
        if (_mode == PollMode.Sync)
        {
            if (_cursor.Length == 0)
                return;
            events = Get(Get(Get(Get(Get(payload, "rooms"), "join"), _joinedRoomId), "timeline"), "events");
        }
        else
            events = Get(payload, "chunk");
        if (events.ValueKind != JsonValueKind.Array)
            return;

        foreach (JsonElement item in events.EnumerateArray())
        {
            if (_mode == PollMode.Messages && GetString(item, "room_id") != _joinedRoomId)
                continue;
            string? sender = GetString(item, "sender");
            JsonElement content = Get(item, "content");
            if (string.IsNullOrEmpty(sender) || sender == _user || GetString(item, "type") != "m.room.message"
                || GetString(content, "msgtype") != "m.text")
                continue;
            if (GetString(content, "body") is { Length: > 0 } body)
                receive(body);
        }
    }

    private async Task<Response> Request(HttpMethod method, string path, object? body, CancellationToken cancellation)
    {
        // Redirects could forward a room message or a room join to a different server.
        // A loopback homeserver is local even when it uses TLS.
        _http ??= new HttpClient(new HttpClientHandler
        {
            AllowAutoRedirect = false,
            UseCookies = false,
            UseProxy = !_server!.IsLoopback
        })
        {
            Timeout = TimeSpan.FromSeconds(40),
            MaxResponseContentBufferSize = 1024 * 1024
        };
        try
        {
            using var request = new HttpRequestMessage(method, _server!.AbsoluteUri.TrimEnd('/') + ApiPath + path);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _token);
            if (body is not null)
                request.Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json");
            using HttpResponseMessage response = await _http.SendAsync(request, cancellation).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
                return new((int)response.StatusCode, default);
            if (method == HttpMethod.Get || path.EndsWith("/join", StringComparison.Ordinal)
                || path.StartsWith("/join/", StringComparison.Ordinal))
            {
                string json = await response.Content.ReadAsStringAsync(cancellation).ConfigureAwait(false);
                using JsonDocument document = JsonDocument.Parse(json);
                return new((int)response.StatusCode, document.RootElement.Clone());
            }
            return new((int)response.StatusCode, default);
        }
        catch (Exception ex) when (ex is OperationCanceledException or HttpRequestException or JsonException
            or FormatException or InvalidOperationException)
        {
            // Exception text and response bodies can contain server-supplied data or request details.
            return default;
        }
    }

    private static JsonElement Get(JsonElement parent, string key)
        => parent.ValueKind == JsonValueKind.Object && parent.TryGetProperty(key, out JsonElement item) ? item : default;

    private static string? GetString(JsonElement parent, string key)
        => Get(parent, key) is { ValueKind: JsonValueKind.String } item ? item.GetString() : null;

    private static string Escape(string value) => Uri.EscapeDataString(value);

    private static void ReportFailure(Response response, string action)
        => Log.Warn(response.Status == 0 || response.Success
            ? $"[Matrix] {action} failed."
            : $"[Matrix] {action} failed (HTTP {response.Status}).");

    private void ResetPoll(PollMode mode)
    {
        Cancel(ref _pollCancellation);
        _pollTask = null;
        _cursor = "";
        _nextPoll = 0;
        _reportedPollFailure = false;
        _mode = mode;
    }

    private void DropRoomMessages()
    {
        // Preserve global profile writes and in-flight membership operations.
        int pending = _writes.Count;
        while (pending-- > 0 && _writes.TryDequeue(out Write write))
            if (write.Kind == WriteKind.Nick)
                _writes.Enqueue(write);
        if (_writeTask is not null && _writing.Kind is WriteKind.Say or WriteKind.Typing)
        {
            Cancel(ref _writeCancellation);
            _writeTask = null;
        }
    }

    private void ResetMembership()
    {
        Cancel(ref _writeCancellation);
        _writeTask = null;
        _writes.Clear();
        _retryWrite = null;
        _nextWrite = 0;
        _joinedRoom = _joinedRoomId = _forgetRoomId = _rejectedJoin = "";
        _forgetOnDeparture = false;
    }

    private void ResetHttp()
    {
        _http?.Dispose();
        _http = null;
    }

    private static void Cancel(ref CancellationTokenSource? source)
    {
        source?.Cancel();
        source?.Dispose();
        source = null;
    }

    public void Dispose()
    {
        ResetPoll(PollMode.Off);
        ResetMembership();
        ResetHttp();
        _token = _user = _requestedRoom = "";
    }
}
