"""Relay WebRTC signaling between the Screen Bridge page and Unity.

Every text message from one client is forwarded to all other clients.
Messages are JSON: {"type": "ready"} from Unity, {"type": "offer", "sdp"} from
the page, {"type": "answer", "sdp"} from Unity. ICE candidates ride inside the SDP.

    python signaling.py            # ws://0.0.0.0:8766
"""

import asyncio

from websockets.asyncio.server import broadcast, serve

clients = set()


async def handler(ws):
    clients.add(ws)
    try:
        async for message in ws:
            broadcast(clients - {ws}, message)
    finally:
        clients.discard(ws)


async def main():
    async with serve(handler, "0.0.0.0", 8766) as server:
        print("signaling on ws://0.0.0.0:8766")
        await server.serve_forever()


if __name__ == "__main__":
    asyncio.run(main())
