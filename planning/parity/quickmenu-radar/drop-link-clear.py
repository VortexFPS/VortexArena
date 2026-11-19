"""Loopback UDP relay that drops the first datagram carrying the fixture's reliable clear."""
from pathlib import Path
import re
import socket
import sys

server = ("127.0.0.1", int(sys.argv[1]))
directory = Path(sys.argv[2])
relay = socket.socket(socket.AF_INET, socket.SOCK_DGRAM)
relay.bind(("127.0.0.1", 0))
print(f"LINK_RELAY {relay.getsockname()[1]}", flush=True)
client = None
clear = directory / "link-clear"
pattern = None
dropped = delivered = False
while True:
    packet, peer = relay.recvfrom(65535)
    if peer != server:
        if client is None:
            client = peer
        if peer == client:
            relay.sendto(packet, server)
        continue
    if client is None:
        continue
    if pattern is None and clear.is_file():
        payload = bytes.fromhex(clear.read_text())
        # ENet SEND_RELIABLE (ack flag), channel 0, two-byte command sequence, then payload length.
        pattern = re.compile(rb"\x86\x00..\x00\x06" + re.escape(payload), re.DOTALL)
    if pattern is not None and not delivered and pattern.search(packet):
        if not dropped:
            print(f"LINK_DROP_CLEAR {payload.hex()}", flush=True)
            dropped = True
            continue
        print(f"LINK_DELIVER_RETRY {payload.hex()}", flush=True)
        delivered = True
    relay.sendto(packet, client)
