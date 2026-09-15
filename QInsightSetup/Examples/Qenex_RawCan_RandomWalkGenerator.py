#!/usr/bin/env python3
"""Random-walk CAN generator - the counterpart of QInsight's Raw CAN Frames protocol.

Runs on a Linux machine with a SocketCAN interface, typically a Raspberry Pi with a CAN HAT
(QInsight sits on the other end of the bus with a PEAK PCAN-USB adapter):

    sudo ip link set can0 up type can bitrate 250000
    python3 Qenex_RawCan_RandomWalkGenerator.py              # can0, 50 sets of frames per second
    python3 Qenex_RawCan_RandomWalkGenerator.py --channel can1 --hz 20

Shipped with QInsight in the Examples folder; used by the example
Qenex_CAN_RawCAN_PythonRandomWalks.qproj.

Frames sent (standard 11-bit ids, DLC 8, little-endian):
    id 0x100 .. 0x104   rnd1 .. rnd5   bytes 0-3 float32 value, bytes 4-7 float32 time t [s]
Each signal is a random walk: every sample moves by a random step in +-step and a weak
mean reversion pulls it back towards its offset O, so it never drifts off the chart.

Frames accepted (what QInsight sends for a Raw CAN variable with direction="write"):
    id 0x180 .. 0x184   bytes 0-3 float32   new offset O of rnd1 .. rnd5
    id 0x190 .. 0x194   bytes 0-3 float32   new step size of rnd1 .. rnd5
The walk jumps to the new offset at once, so a write is visible immediately on the graph.
"""
import argparse
import random
import socket
import struct
import threading
import time

# id -> random-walk parameters
#   O          = offset (the value the walk hovers around)
#   step       = max size of one random step; each sample moves by +-step
#   reversion  = mean-reversion strength (0..1); pulls the value back towards O
SIGNALS = {
    0x100: {"O":  0.0, "step": 0.5, "reversion": 0.01},
    0x101: {"O": 10.0, "step": 1.0, "reversion": 0.02},
    0x102: {"O": -5.0, "step": 0.3, "reversion": 0.015},
    0x103: {"O": 20.0, "step": 2.0, "reversion": 0.01},
    0x104: {"O":  5.0, "step": 0.8, "reversion": 0.03},
}
OFFSET_ID_BASE = 0x180   # 0x180 + n sets O of the n-th signal
STEP_ID_BASE = 0x190     # 0x190 + n sets step of the n-th signal

FRAME_FORMAT = "=IB3x8s"  # SocketCAN frame: id (4), dlc (1), padding (3), data (8)
CAN_EFF_FLAG = 0x80000000

lock = threading.Lock()
STATE = {cid: p["O"] for cid, p in SIGNALS.items()}   # current value of every walk


def step_walk(can_id):
    """Advance one random walk by one step and return the new value."""
    with lock:
        p = SIGNALS[can_id]
        x = STATE[can_id]
        x = x + random.uniform(-p["step"], p["step"]) + p["reversion"] * (p["O"] - x)
        STATE[can_id] = x
        return x


def build_frame(can_id, value, t):
    data = struct.pack("<ff", value, t)        # 8 B: float32 value + float32 t
    return struct.pack(FRAME_FORMAT, can_id, len(data), data)


def apply_frame(raw):
    """A received frame: 0x18n / 0x19n with a float32 sets offset / step of signal n."""
    can_id, dlc, data = struct.unpack(FRAME_FORMAT, raw)
    if can_id & CAN_EFF_FLAG or dlc < 4:
        return
    can_id &= 0x7FF
    for base, key in ((OFFSET_ID_BASE, "O"), (STEP_ID_BASE, "step")):
        n = can_id - base
        if 0 <= n < len(SIGNALS):
            signal_id = 0x100 + n
            value = struct.unpack("<f", data[:4])[0]
            with lock:
                SIGNALS[signal_id][key] = value
                if key == "O":
                    STATE[signal_id] = value      # jump there, so the write shows at once
            print(f"rnd{n + 1}.{key} = {value:.3f}", flush=True)
            return


def reader_thread(sock, stop):
    while not stop.is_set():
        try:
            raw = sock.recv(16)
        except OSError:
            break
        if len(raw) == 16:
            apply_frame(raw)


def main():
    parser = argparse.ArgumentParser(description="Raw CAN random-walk generator for QInsight (SocketCAN).")
    parser.add_argument("--channel", default="can0", help="SocketCAN interface (default can0)")
    parser.add_argument("--hz", type=float, default=50.0, help="sets of 5 frames per second (default 50)")
    args = parser.parse_args()

    sock = socket.socket(socket.AF_CAN, socket.SOCK_RAW, socket.CAN_RAW)
    sock.bind((args.channel,))
    stop = threading.Event()
    threading.Thread(target=reader_thread, args=(sock, stop), daemon=True).start()

    t0 = time.monotonic()
    period = 1.0 / args.hz
    next_t = time.monotonic()
    print(f"Raw CAN generator on {args.channel}: ids 0x100-0x104 @ {args.hz:g} Hz, "
          f"writes on 0x180-0x184 (offset) and 0x190-0x194 (step). Ctrl+C to stop.", flush=True)
    try:
        while True:
            t = time.monotonic() - t0
            for can_id in SIGNALS:                 # 5 frames, one signal each
                sock.send(build_frame(can_id, step_walk(can_id), t))
            next_t += period
            time.sleep(max(0.0, next_t - time.monotonic()))
    except KeyboardInterrupt:
        pass
    finally:
        stop.set()
        sock.close()


if __name__ == "__main__":
    main()
