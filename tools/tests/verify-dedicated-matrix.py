#!/usr/bin/env python3
"""Run a native dedicated map and one native client against a loopback-only Matrix fixture."""

import argparse
from contextlib import contextmanager
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
import json
import os
from pathlib import Path
from queue import Empty, Queue
import select
import socket
import subprocess
import tempfile
import threading
import time
from urllib.parse import parse_qs, unquote, urlsplit

ROOT = Path(__file__).resolve().parents[2]
PREFIX = "/_matrix/client/r0"
TOKEN = "local-fixture-token"
BOT = "@bridge:fixture"
ROOM = "!one:fixture"
OTHER = "!two:fixture"
THIRD = "!three:fixture"


def event(body, sender="@human:fixture", room=ROOM, kind="m.text"):
    return {
        "type": "m.room.message",
        "sender": sender,
        "room_id": room,
        "content": {"msgtype": kind, "body": body},
    }


def timeline(*events):
    return {"timeline": {"events": events}}


class MatrixFixture(ThreadingHTTPServer):
    def __init__(self):
        super().__init__(("127.0.0.1", 0), Handler)
        self.writes = Queue()
        self.sent_messages = Queue()
        self.polls = Queue()
        self.sync = Queue()
        self.events = Queue()
        self.errors = Queue()
        self.memberships = set()
        self.write_holds = {}
        self.poll_hold = None
        self.lock = threading.Lock()

    def hold_write(self, path):
        hold = Hold()
        with self.lock:
            self.write_holds[path] = hold
        return hold

    def hold_poll(self):
        hold = Hold()
        with self.lock:
            self.poll_hold = hold
        return hold

    def current_rooms(self):
        with self.lock:
            return set(self.memberships)

    @property
    def url(self):
        return f"http://127.0.0.1:{self.server_port}"


class Hold:
    def __init__(self):
        self.started = threading.Event()
        self.release = threading.Event()
        self.closed = threading.Event()

    def wait(self, connection):
        self.started.set()
        end = time.monotonic() + 12
        while not self.release.is_set() and time.monotonic() < end:
            ready, _, _ = select.select([connection], [], [], 0.05)
            if ready:
                try:
                    if not connection.recv(1, socket.MSG_PEEK):
                        self.closed.set()
                        return
                except (ConnectionResetError, BrokenPipeError):
                    self.closed.set()
                    return


class ProxyProbe(BaseHTTPRequestHandler):
    def reject(self):
        self.server.requests.put(self.path)
        self.send_error(502)

    do_GET = do_POST = do_PUT = do_CONNECT = reject

    def log_message(self, *_):
        pass


class Handler(BaseHTTPRequestHandler):
    def log_message(self, *_):
        pass

    def handle_request(self):
        url = urlsplit(self.path)
        query = parse_qs(url.query, keep_blank_values=True)
        path = unquote(url.path)
        if TOKEN in self.path or self.headers.get("Authorization") != f"Bearer {TOKEN}":
            self.server.errors.put("Credentials must appear only in the authorization header")
            return 401, {}
        if self.command != "GET":
            body = json.loads(self.rfile.read(int(self.headers["Content-Length"])))
            joined = None
            with self.server.lock:
                if path.endswith("/join"):
                    joined = path.split("/")[-2]
                elif path == f"{PREFIX}/join/#two:fixture":
                    joined = OTHER
                if joined is not None:
                    self.server.memberships.add(joined)
                elif path.endswith("/leave"):
                    self.server.memberships.discard(path.split("/")[-2])
                elif path.endswith("/forget") and path.split("/")[-2] in self.server.memberships:
                    self.server.errors.put("Forget attempted before leaving the joined room")
                    return 400, {}
                hold = self.server.write_holds.pop(path, None)
            self.server.writes.put((self.command, path, body))
            if "/send/m.room.message/" in path:
                self.server.sent_messages.put(body["body"])
            if hold:
                hold.wait(self.connection)
            return 200, {} if joined is None else {"room_id": joined}

        if path == f"{PREFIX}/sync":
            since = query.get("since", [""])[0]
            rooms = json.loads(query["filter"][0])["room"]["rooms"]
            self.server.polls.put(("sync", since, rooms, query["timeout"][0]))
            if not since:
                return 200, {
                    "next_batch": "initial+?&",
                    "rooms": {"join": {ROOM: timeline(event("initial-backlog-marker"))}},
                }
            with self.server.lock:
                hold = self.server.poll_hold
                self.server.poll_hold = None
            if hold:
                hold.wait(self.connection)
            try:
                return self.server.sync.get(timeout=0.35)
            except Empty:
                return 200, {"next_batch": since}
        if path == f"{PREFIX}/events":
            room = query["room_id"][0]
            start = query["from"][0]
            self.server.polls.put(("events", start, [room], query["timeout"][0]))
            if query["limit"] != ["50"]:
                self.server.errors.put("Unexpected /events limit")
            try:
                return self.server.events.get(timeout=0.35)
            except Empty:
                return 200, {"end": start or "e0", "chunk": []}
        self.server.errors.put(f"Unexpected HTTP request {path}")
        return 404, {}

    def dispatch(self):
        status, body = self.handle_request()
        data = json.dumps(body).encode()
        try:
            self.send_response(status)
            self.send_header("Content-Type", "application/json")
            self.send_header("Content-Length", str(len(data)))
            self.end_headers()
            self.wfile.write(data)
        except (BrokenPipeError, ConnectionResetError):
            pass  # expected when mx leave, mx token or map shutdown cancels a long poll

    do_GET = do_PUT = do_POST = dispatch


def expect(queue, predicate, timeout=12):
    end = time.monotonic() + timeout
    seen = []
    while time.monotonic() < end:
        try:
            value = queue.get(timeout=max(0, end - time.monotonic()))
        except Empty:
            break
        if predicate(value):
            return value
        seen.append(value)
    raise AssertionError(f"Expected fixture request; observed {seen!r}")


def wait_log(path, marker, timeout=30, count=1):
    end = time.monotonic() + timeout
    while time.monotonic() < end:
        if path.read_text(errors="replace").count(marker) >= count:
            return
        time.sleep(0.05)
    raise AssertionError(f"{marker!r} missing from {path}")


def drain(queue):
    while True:
        try:
            queue.get_nowait()
        except Empty:
            return


@contextmanager
def launch(godot, work, name, *args):
    env = os.environ | {
        "VORTEX_USERDIR": str(work / f"{name}-user"),
        "XDG_DATA_HOME": str(work / f"{name}-xdg"),
        "DOTNET_ROLL_FORWARD": "Major",
        "NO_PROXY": "",
        "no_proxy": "",
    }
    log = work / f"{name}.log"
    with log.open("wb") as output, subprocess.Popen(
        [godot, "--headless", "--path", str(ROOT), "--audio-driver", "Dummy",
         "--no-save-config", *args],
        env=env, stdin=subprocess.PIPE, stdout=output, stderr=subprocess.STDOUT, text=True,
    ) as process:
        def send(command):
            process.stdin.write(command + "\n")
            process.stdin.flush()

        try:
            yield send, log
        finally:
            if process.poll() is None:
                try:
                    if "--dedicated" in args or "--host" in args:
                        send("quit")
                    else:
                        process.terminate()
                    process.wait(timeout=5)
                except (BrokenPipeError, subprocess.TimeoutExpired):
                    process.kill()
                    process.wait(timeout=5)


def game_port():
    with socket.socket(socket.AF_INET, socket.SOCK_DGRAM) as sock:
        sock.bind(("127.0.0.1", 0))
        return str(sock.getsockname()[1])


def expect_write(matrix, path):
    method, requested_path, body = matrix.writes.get(timeout=12)
    assert requested_path == PREFIX + path, (method, requested_path, body, path)


def verify_room_transitions(send, server_log, matrix):
    def barrier(*commands):
        status_count = server_log.read_text(errors="replace").count("gametype: tdm")
        send("\n".join((*commands, "status")))
        wait_log(server_log, "gametype: tdm", count=status_count + 1)

    # Hold a profile request so A→B→C arrives before any membership write drains.
    blocker = matrix.hold_write(f"{PREFIX}/profile/{BOT}/displayname")
    send("mx nick transition-blocker")
    expect_write(matrix, f"/profile/{BOT}/displayname")
    assert blocker.started.wait(3)
    barrier("mx join #two:fixture", "mx say skipped-room-marker", f"mx join {THIRD}")
    assert matrix.current_rooms() == {ROOM}
    drain(matrix.polls)
    blocker.release.set()
    expect_write(matrix, f"/rooms/{ROOM}/leave")
    expect_write(matrix, f"/rooms/{THIRD}/join")
    expect(matrix.polls, lambda req: req == ("sync", "initial+?&", [THIRD], "30000"))
    assert matrix.current_rooms() == {THIRD}

    # Change the selection again while the current canonical room is being left.
    drain(matrix.polls)
    leaving = matrix.hold_write(f"{PREFIX}/rooms/{THIRD}/leave")
    send("mx join #two:fixture")
    expect_write(matrix, f"/rooms/{THIRD}/leave")
    assert leaving.started.wait(3)
    barrier("mx say skipped-during-leave-marker", f"mx join {ROOM}")
    leaving.release.set()
    expect_write(matrix, f"/rooms/{ROOM}/join")
    expect(matrix.polls, lambda req: req == ("sync", "initial+?&", [ROOM], "30000"))
    assert matrix.current_rooms() == {ROOM}

    # The homeserver joins the alias immediately but withholds its canonical ID.
    # A later selection still has to leave the canonical alias target.
    joining = matrix.hold_write(f"{PREFIX}/join/#two:fixture")
    send("mx join #two:fixture")
    expect_write(matrix, f"/rooms/{ROOM}/leave")
    expect_write(matrix, "/join/#two:fixture")
    assert joining.started.wait(3)
    assert matrix.current_rooms() == {OTHER}
    barrier("mx say superseded-alias-marker", f"mx join {THIRD}")
    drain(matrix.polls)
    assert matrix.writes.empty() and matrix.polls.empty()
    assert not joining.closed.is_set()
    joining.release.set()
    expect_write(matrix, f"/rooms/{OTHER}/leave")
    expect_write(matrix, f"/rooms/{THIRD}/join")
    expect(matrix.polls, lambda req: req == ("sync", "initial+?&", [THIRD], "30000"))
    assert matrix.current_rooms() == {THIRD}

    # Stop and forget another in-flight join, then select A while cleanup is pending.
    joining = matrix.hold_write(f"{PREFIX}/join/#two:fixture")
    send("mx join #two:fixture")
    expect_write(matrix, f"/rooms/{THIRD}/leave")
    expect_write(matrix, "/join/#two:fixture")
    assert joining.started.wait(3)
    assert matrix.current_rooms() == {OTHER}
    barrier("mx say skipped-during-join-marker", "mx forget", f"mx join {ROOM}", "mx sync")
    drain(matrix.polls)
    time.sleep(0.2)
    assert matrix.writes.empty() and matrix.polls.empty()
    assert not joining.closed.is_set()
    joining.release.set()
    expect_write(matrix, f"/rooms/{OTHER}/leave")
    expect_write(matrix, f"/rooms/{OTHER}/forget")
    expect_write(matrix, f"/rooms/{ROOM}/join")
    expect(matrix.polls, lambda req: req == ("sync", "initial+?&", [ROOM], "30000"))
    assert matrix.current_rooms() == {ROOM}


def verify(godot, work, matrix):
    port = game_port()
    with launch(godot, work, "server", "--dedicated", "darkzone", "--gametype", "tdm",
                "--port", port, "--bots", "1", "--cvar", "sv_public", "0",
                "--cvar", "g_warmup", "0", "--cvar", "g_maplist_votable", "0") as (send, server_log):
        wait_log(server_log, "reading commands from stdin")
        for command in (
            "mx token refused-by-default",
            f"matrix_server {matrix.url}",
            f"mx user {BOT}",
            f"mx join {ROOM}",
            "mx say off-by-default-marker",
        ):
            send(command)
        wait_log(server_log, "Matrix requires a valid token")
        send("status")
        wait_log(server_log, "gametype: tdm")
        assert matrix.writes.empty() and matrix.polls.empty()

        send(f"sv_cmd mx token {TOKEN}")
        expect(matrix.writes, lambda req: req == ("POST", f"{PREFIX}/rooms/{ROOM}/join", {}))
        send('mx nick "Fixture  nick"')
        expect(matrix.writes, lambda req: req == (
            "PUT", f"{PREFIX}/profile/{BOT}/displayname", {"displayname": "Fixture  nick"}))
        send("mx typing")
        expect(matrix.writes, lambda req: req == (
            "PUT", f"{PREFIX}/rooms/{ROOM}/typing/{BOT}", {"typing": True, "timeout": 30000}))

        with launch(godot, work, "client", "--connect", f"127.0.0.1:{port}") as (_, client_log):
            wait_log(server_log, "accepted as")
            send("bot_cmd 1 cc say from-game-marker")
            game = expect(matrix.writes, lambda req: req[2].get("body", "").endswith("^7: from-game-marker"))
            assert game[1].startswith(f"{PREFIX}/rooms/{ROOM}/send/m.room.message/")
            send("bot_cmd 1 cc say_team private-team-marker")
            send("say dedicated-console-marker")
            send('mx say explicit  "quoted" text')
            explicit = expect(matrix.writes, lambda req: req[2].get("msgtype") == "m.text")
            assert explicit[2]["body"] == 'explicit  "quoted" text'
            assert explicit[1] != game[1] and explicit[1].startswith(f"{PREFIX}/rooms/{ROOM}/send/m.room.message/")

            send("sv_cmd mx sync")
            expect(matrix.polls, lambda req: req == ("sync", "", [], "0"))
            expect(matrix.polls, lambda req: req == ("sync", "initial+?&", [ROOM], "30000"))
            matrix.sync.put((503, {"error": TOKEN}))
            wait_log(server_log, "poll failed (HTTP 503)")
            drain(matrix.polls)
            expect(matrix.polls, lambda req: req == ("sync", "initial+?&", [ROOM], "30000"))
            matrix.sync.put((200, {"next_batch": "s1", "rooms": {"join": {
                ROOM: timeline(event("matrix-sync-marker"), event("matrix-self-echo-marker", BOT),
                               event("matrix-image-marker", kind="m.image")),
                OTHER: timeline(event("matrix-wrong-room-marker", room=OTHER)),
            }}}))
            wait_log(client_log, "matrix-sync-marker")
            expect(matrix.polls, lambda req: req == ("sync", "s1", [ROOM], "30000"))

            verify_room_transitions(send, server_log, matrix)

            # Switching rooms cancels the old poll and leaves the canonical old room before joining the alias.
            send("mx join #two:fixture")
            assert expect(matrix.writes, lambda req: req[0] == "POST")[1] == f"{PREFIX}/rooms/{ROOM}/leave"
            assert expect(matrix.writes, lambda req: req[0] == "POST")[1] == f"{PREFIX}/join/#two:fixture"
            expect(matrix.polls, lambda req: req == ("sync", "initial+?&", [OTHER], "30000"))
            send("mx say second-room-marker")
            second = expect(matrix.writes, lambda req: req[2].get("body") == "second-room-marker")
            assert second[1].startswith(f"{PREFIX}/rooms/{OTHER}/send/m.room.message/")

            send("mx messages")
            expect(matrix.polls, lambda req: req == ("events", "", [OTHER], "30000"))
            matrix.events.put((200, {"end": "e1", "chunk": [
                event("matrix-events-marker", room=OTHER),
                event("matrix-wrong-event-room-marker"),
                event("matrix-events-self-echo-marker", sender=BOT, room=OTHER),
            ]}))
            wait_log(client_log, "matrix-events-marker")
            expect(matrix.polls, lambda req: req == ("events", "e1", [OTHER], "30000"))

            send("mx leave")
            expect(matrix.writes, lambda req: req[1] == f"{PREFIX}/rooms/{OTHER}/leave")
            matrix.events.put((200, {"end": "e2", "chunk": [event("stopped-poll-marker", room=OTHER)]}))
            time.sleep(0.6)
            drain(matrix.polls)
            time.sleep(0.6)
            assert matrix.polls.empty()
            send(f"mx join {ROOM}")
            expect(matrix.writes, lambda req: req[1] == f"{PREFIX}/rooms/{ROOM}/join")
            send("mx sync")
            expect(matrix.polls, lambda req: req == ("sync", "initial+?&", [ROOM], "30000"))
            matrix.sync.put((200, {"next_batch": "s2", "rooms": {"join": {
                ROOM: timeline(event("matrix-restarted-marker")),
            }}}))
            wait_log(client_log, "matrix-restarted-marker")
            send("mx token")
            send("mx say cleared-token-marker")
            send("mx sync")
            time.sleep(0.6)
            drain(matrix.polls)
            time.sleep(0.6)
            assert matrix.polls.empty() and matrix.writes.empty()

            # Setting the token again rejoins the selected room. No poll starts before mx sync/messages.
            send(f"mx token {TOKEN}")
            expect(matrix.writes, lambda req: req[1] == f"{PREFIX}/rooms/{ROOM}/join")
            assert matrix.polls.empty()
            send("mx forget")
            expect_write(matrix, f"/rooms/{ROOM}/leave")
            expect_write(matrix, f"/rooms/{ROOM}/forget")

            # A newly built dedicated world needs credentials again, and the old long poll must stop.
            send(f"mx join {ROOM}")
            expect(matrix.writes, lambda req: req[1] == f"{PREFIX}/rooms/{ROOM}/join")
            long_poll = matrix.hold_poll()
            send("mx sync")
            expect(matrix.polls, lambda req: req == ("sync", "initial+?&", [ROOM], "30000"))
            assert long_poll.started.wait(3)
            send("map darkzone")
            wait_log(server_log, "DEDICATED: no local client", count=2)
            assert long_poll.closed.wait(3), "Map teardown did not cancel the active Matrix HTTP request"
            time.sleep(1)
            drain(matrix.polls)
            send(f"mx user {BOT}")
            send(f"mx join {ROOM}")
            send("mx sync")
            send("mx say retired-world-marker")
            time.sleep(0.8)
            assert matrix.polls.empty() and matrix.writes.empty()
            # A loopback HTTPS homeserver receives the TLS ClientHello directly.
            # Closing here keeps the check independent of machine certificate stores.
            with socket.create_server(("127.0.0.1", 0)) as tls_probe:
                tls_probe.settimeout(10)
                send(f"matrix_server https://127.0.0.1:{tls_probe.getsockname()[1]}")
                send(f"mx token {TOKEN}")
                send(f"mx join {ROOM}")
                connection, _ = tls_probe.accept()
                with connection:
                    connection.settimeout(5)
                    header = connection.recv(6, socket.MSG_WAITALL)
                    assert len(header) == 6 and header[:2] == b"\x16\x03" and header[5] == 1, header
            output = client_log.read_text(errors="replace")
            for marker in ("initial-backlog-marker", "matrix-self-echo-marker", "matrix-image-marker",
                           "matrix-wrong-room-marker", "matrix-wrong-event-room-marker",
                           "matrix-events-self-echo-marker", "stopped-poll-marker"):
                assert marker not in output, marker
            assert TOKEN not in output
        assert TOKEN not in server_log.read_text(errors="replace")

    with launch(godot, work, "listen", "--host", "darkzone", "--port", game_port(),
                "--cvar", "sv_public", "0") as (send, log):
        wait_log(log, "reading commands from stdin")
        send(f"matrix_server {matrix.url}")
        send(f"mx token {TOKEN}")
        send(f"mx join {ROOM}")
        wait_log(log, 'Unknown command "mx"')
        assert matrix.writes.empty() and matrix.polls.empty()
        assert TOKEN not in log.read_text(errors="replace")

    assert matrix.errors.empty(), matrix.errors.queue
    messages = list(matrix.sent_messages.queue)
    assert len(messages) == 3, messages
    assert messages[0].endswith("^7: from-game-marker")
    assert messages[1:] == ['explicit  "quoted" text', "second-room-marker"]


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--godot", required=True, help="Path to the Godot .NET executable")
    args = parser.parse_args()
    work = Path(tempfile.mkdtemp(prefix="vortex-dedicated-matrix-"))
    print(f"Logs: {work}", flush=True)
    with MatrixFixture() as matrix, ThreadingHTTPServer(("127.0.0.1", 0), ProxyProbe) as proxy:
        proxy.requests = Queue()
        proxy_url = f"http://127.0.0.1:{proxy.server_port}"
        for key in ("http_proxy", "HTTP_PROXY", "https_proxy", "HTTPS_PROXY", "all_proxy", "ALL_PROXY"):
            os.environ[key] = proxy_url
        servers = (matrix, proxy)
        threads = [threading.Thread(target=server.serve_forever, daemon=True) for server in servers]
        for thread in threads:
            thread.start()
        try:
            verify(args.godot, work, matrix)
            assert proxy.requests.empty(), "Loopback Matrix traffic reached the configured proxy"
        finally:
            for server, thread in zip(servers, threads):
                server.shutdown()
                thread.join()
    print("Native dedicated Matrix verification passed")


if __name__ == "__main__":
    main()
