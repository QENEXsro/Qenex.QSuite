#!/usr/bin/env python3
"""Random-walk signal CAN master for Raspberry Pi.

Vysila 5 random-walk signalu jako samostatne CAN framy:
    rnd1 -> 0x100, rnd2 -> 0x101, ... rnd5 -> 0x104

Kazdy signal je random walk: kazdy vzorek se posune o nahodny krok v +-step,
slaba mean-reversion (rev) ho drzi kolem offsetu O, aby neuletel z grafu.

Payload (8B): float32 hodnota (little-endian) + float32 cas t (monotonic).
    struct.pack("<ff", value, t)

Pred spustenim nahodit CAN rozhrani, napr.:
    sudo ip link set can0 up type can bitrate 500000
"""
import socket
import struct
import random
import time

CHANNEL = "can0"
SEND_HZ = 50                       # kolik kompletnich sad (5 framu) za sekundu

# id -> parametry random walku
#   O          = offset (stred, kolem ktereho walk hovers)
#   step       = max velikost jednoho kroku; vzorek se posune o +-step
#   reversion  = sila mean-reversion (0..1); tahne hodnotu zpet k O
SIGNALS = {
    0x100: {"O":   0.0, "step": 0.5, "reversion": 0.01},
    0x101: {"O":  10.0, "step": 1.0, "reversion": 0.02},
    0x102: {"O":  -5.0, "step": 0.3, "reversion": 0.015},
    0x103: {"O":  20.0, "step": 2.0, "reversion": 0.01},
    0x104: {"O":   5.0, "step": 0.8, "reversion": 0.03},
}

# aktualni stav walku; start na offsetu
STATE = {cid: p["O"] for cid, p in SIGNALS.items()}


def step_walk(can_id):
    """Posune random walk o jeden krok a vrati novou hodnotu."""
    p = SIGNALS[can_id]
    x = STATE[can_id]
    x = x + random.uniform(-p["step"], p["step"]) + p["reversion"] * (p["O"] - x)
    STATE[can_id] = x
    return x


def build_frame(can_id, value, t):
    """Sestavi SocketCAN frame: <ID(4) DLC(1) pad(3) data(8)>."""
    data = struct.pack("<ff", value, t)        # 8B: float32 value + float32 t
    return struct.pack("=IB3x8s", can_id, len(data), data)


def main():
    s = socket.socket(socket.AF_CAN, socket.SOCK_RAW, socket.CAN_RAW)
    s.bind((CHANNEL,))

    t0 = time.monotonic()
    period = 1.0 / SEND_HZ
    next_t = time.monotonic()
    print(f"CAN master vysila na {CHANNEL}, ID 0x100-0x104 @ {SEND_HZ} Hz")
    try:
        while True:
            t = time.monotonic() - t0
            for can_id in SIGNALS:                 # 5 framu, jeden signal kazdy
                frame = build_frame(can_id, step_walk(can_id), t)
                s.send(frame)
            next_t += period
            time.sleep(max(0, next_t - time.monotonic()))
    except KeyboardInterrupt:
        pass
    finally:
        s.close()


main()
