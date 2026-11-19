"""Observe ClientInit on a local, uncompressed ENet fixture and optionally alter its visual tail."""
import argparse
from pathlib import Path
from selectors import DefaultSelector, EVENT_READ
from socket import AF_INET, SOCK_DGRAM, socket
import struct

COMMAND_SIZES = (0, 8, 48, 44, 8, 4, 6, 8, 24, 8, 12, 16, 24)
DATA_LENGTH_FIELD = {6: 4, 7: 6, 8: 6, 9: 6, 12: 6}
HANDSHAKE_REQUEST, HANDSHAKE_REJECT, CLIENT_INIT, MAP_VISUAL_SUBSCRIPTION = 1, 3, 21, 34
CLIENT_INIT_FOG_LENGTH_OFFSET = 29
CLIENT_INIT_FIXED_SIZE = 35


def packet_frames(datagram):
    header, = struct.unpack_from("!H", datagram)
    if header & 0x4000:
        raise ValueError("Expected uncompressed ENet traffic")
    cursor = 4 if header & 0x8000 else 2
    while cursor < len(datagram):
        command = datagram[cursor] & 0x0F
        end = cursor + COMMAND_SIZES[command]
        if command in DATA_LENGTH_FIELD:
            length_at = cursor + DATA_LENGTH_FIELD[command]
            length, = struct.unpack_from("!H", datagram, length_at)
            if command == 6:
                yield length_at, end, end + length
            end += length
        if end <= cursor or end > len(datagram):
            raise ValueError("Invalid ENet fixture frame length")
        cursor = end


def inspect(datagram, peer_id, incoming, args, withdrawals):
    updated = bytearray(datagram)
    for length_at, start, end in reversed(list(packet_frames(datagram))):
        message = datagram[start:end]
        opcode = message[0] if message else None
        if incoming and opcode == MAP_VISUAL_SUBSCRIPTION:
            print(f"peer {peer_id} subscription {message[1:].hex()}", flush=True)
            if message[1:] == b"\x00":
                withdrawals.add(peer_id)
        if incoming and opcode == HANDSHAKE_REQUEST and args.legacy_parity is not None:
            print(f"peer {peer_id} sent v27 parity {args.legacy_parity:08x}", flush=True)
            struct.pack_into("<I", updated, start + 1, args.legacy_parity)
        if not incoming and opcode == CLIENT_INIT:
            fog_length, = struct.unpack_from("<H", message, CLIENT_INIT_FOG_LENGTH_OFFSET)
            tail_at = CLIENT_INIT_FIXED_SIZE + fog_length
            tail = message[tail_at:]
            print(f"peer {peer_id} ClientInit tail={tail.hex() or 'absent'}", flush=True)
            if not tail and peer_id in withdrawals and args.withdrawal_marker:
                args.withdrawal_marker.touch()
            if args.mode and tail:
                replacement = b"" if args.mode == "missing" else b"\xff"
                updated[start + tail_at:end] = replacement
                struct.pack_into("!H", updated, length_at, tail_at + len(replacement))
                print(f"peer {peer_id} forwarded tail={replacement.hex() or 'absent'}", flush=True)
                args.marker.touch()
        if not incoming and opcode == HANDSHAKE_REJECT:
            reason_length, = struct.unpack_from("<H", message, 1)
            reason = message[3:3 + reason_length].decode()
            print(f"peer {peer_id} HandshakeReject: {reason}", flush=True)
    return updated


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("listen", type=int)
    parser.add_argument("server", type=int)
    parser.add_argument("--mode", choices=("missing", "malformed"))
    parser.add_argument("--marker", type=Path)
    parser.add_argument("--legacy-parity", type=lambda value: int(value, 16))
    parser.add_argument("--withdrawal-marker", type=Path)
    args = parser.parse_args()
    if args.mode and args.marker is None:
        parser.error("--mode requires --marker")

    downstream = socket(AF_INET, SOCK_DGRAM)
    downstream.bind(("127.0.0.1", args.listen))
    selector = DefaultSelector()
    selector.register(downstream, EVENT_READ)
    upstreams = {}
    withdrawals = set()
    print("relay ready", flush=True)
    while True:
        for key, _ in selector.select():
            if key.fileobj is downstream:
                datagram, address = downstream.recvfrom(65535)
                if address not in upstreams:
                    upstream = socket(AF_INET, SOCK_DGRAM)
                    upstream.connect(("127.0.0.1", args.server))
                    upstreams[address] = upstream, len(upstreams) + 1
                    selector.register(upstream, EVENT_READ, address)
                upstream, peer_id = upstreams[address]
                upstream.send(inspect(datagram, peer_id, True, args, withdrawals))
            else:
                upstream = key.fileobj
                address = key.data
                _, peer_id = upstreams[address]
                datagram = upstream.recv(65535)
                downstream.sendto(inspect(datagram, peer_id, False, args, withdrawals), address)


if __name__ == "__main__":
    main()
