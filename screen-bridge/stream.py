"""Stream the laptop screen as JPEG frames over a WebSocket.

Each binary WS message is one complete JPEG. Clients always get the newest
frame; a slow client skips frames instead of falling behind.

    python stream.py                       # whole main monitor
    python stream.py --region 0,0,1600,1000
    python stream.py --monitor 2 --fps 15

macOS: give your terminal Screen Recording permission, or windows come out blank.
"""

import argparse
import asyncio
import io
import threading
import time

import mss
from PIL import Image
from websockets.asyncio.server import serve

frame = None
new_frame = asyncio.Event()


def publish(jpeg):
    global frame, new_frame
    frame = jpeg
    new_frame.set()
    new_frame = asyncio.Event()


def capture(loop, args):
    with mss.MSS() as sct:
        if args.region:
            left, top, width, height = map(int, args.region.split(","))
            area = {"left": left, "top": top, "width": width, "height": height}
        else:
            area = sct.monitors[args.monitor]
        last = None
        while True:
            start = time.monotonic()
            shot = sct.grab(area)
            img = Image.frombytes("RGB", shot.size, shot.bgra, "raw", "BGRX")
            img.thumbnail((args.max_size, args.max_size))
            buf = io.BytesIO()
            img.save(buf, "JPEG", quality=args.quality)
            jpeg = buf.getvalue()
            # ponytail: skip identical frames so a still screen costs Unity no decode time
            if jpeg != last:
                loop.call_soon_threadsafe(publish, jpeg)
                last = jpeg
            time.sleep(max(0, 1 / args.fps - (time.monotonic() - start)))


async def handler(ws):
    while True:
        # Grab the event before sending, so a frame published mid-send is not missed.
        ready = new_frame
        if frame is not None:
            await ws.send(frame)
        await ready.wait()


async def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--port", type=int, default=8765)
    parser.add_argument("--monitor", type=int, default=1, help="1 = main monitor")
    parser.add_argument("--region", help="left,top,width,height in screen points")
    parser.add_argument("--fps", type=float, default=10)
    parser.add_argument("--quality", type=int, default=80)
    parser.add_argument("--max-size", type=int, default=1920, help="Quest textures max 2048")
    args = parser.parse_args()

    threading.Thread(target=capture, args=(asyncio.get_running_loop(), args), daemon=True).start()
    async with serve(handler, "0.0.0.0", args.port) as server:
        print(f"streaming on ws://0.0.0.0:{args.port}")
        await server.serve_forever()


if __name__ == "__main__":
    asyncio.run(main())
