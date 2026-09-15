#!/usr/bin/env python3
"""Random-walk signal TCP master for Raspberry Pi Zero W.

Master = TCP server. A slave connects over IP:port.
Master continuously sends 5 messages (one signal each). Each signal is a
random walk: every sample the value moves by a random step in +-step, so it
"flies around" some value. A weak mean-reversion pull keeps it near its
offset O so it does not drift off the chart over time.

Wire protocol (newline-delimited JSON):
  Master -> slave:  {"name": "rnd1", "value": 2.13, "t": 0.05}\n   (5x per cycle)
  Slave  -> master: {"cmd": "set", "name": "rnd1", "O": 2.0, "step": 0.5,
                     "reversion": 0.01}\n
                    (all params optional; only the provided ones change)
"""
import socket
import threading
import json
import random
import time

HOST = "0.0.0.0"
PORT = 5000
SEND_HZ = 200            # how often a full set of samples is sent, per second

# Per-signal parameters for a random walk:
#   O          = offset (center the walk hovers around)
#   step       = max size of one random step; each sample moves by +-step
#   reversion  = mean-reversion strength (0..1); pulls value back toward O.
#                0.0 -> pure random walk (will drift). Small values (~0.01)
#                keep it loosely tethered around O without looking "stiff".
INITIAL_PARAMS = {
    "rnd1": {"O":   0.0, "step": 0.5, "reversion": 0.01},
    "rnd2": {"O":  10.0, "step": 1.0, "reversion": 0.02},
    "rnd3": {"O":  -5.0, "step": 0.3, "reversion": 0.015},
    "rnd4": {"O":  20.0, "step": 2.0, "reversion": 0.01},
    "rnd5": {"O":   5.0, "step": 0.8, "reversion": 0.03},
}
SIGNAL_NAMES = list(INITIAL_PARAMS.keys())


class SignalStore:
    """Thread-safe store of per-signal parameters and random-walk state."""

    def __init__(self, initial):
        self._lock = threading.Lock()
        self._sig = {n: dict(p) for n, p in initial.items()}
        # Current value of each walk; start sitting on the offset.
        self._x = {n: p["O"] for n, p in initial.items()}

    def value(self, name, t):
        """Advance the random walk by one step and return the new value.

        Note: this is stateful -- each call moves the walk forward, so it
        must be called exactly once per sample per signal.
        """
        with self._lock:
            p = self._sig[name]
            O, step, rev = p["O"], p["step"], p["reversion"]
            x = self._x[name]
            # Random step in [-step, +step] plus a pull back toward the offset.
            x = x + random.uniform(-step, step) + rev * (O - x)
            self._x[name] = x
            return x

    def set_param(self, name, O=None, step=None, reversion=None):
        with self._lock:
            if name not in self._sig:
                return False
            if O is not None:
                self._sig[name]["O"] = float(O)
            if step is not None:
                self._sig[name]["step"] = float(step)
            if reversion is not None:
                self._sig[name]["reversion"] = float(reversion)
            return True


def reader_thread(conn, store, stop):
    """Listen for commands from the slave and apply parameter changes."""
    conn_file = conn.makefile("rb")
    try:
        for raw in conn_file:                 # read line by line
            if stop.is_set():
                break
            line = raw.strip()
            if not line:
                continue
            try:
                msg = json.loads(line.decode("utf-8"))
            except json.JSONDecodeError:
                continue
            if msg.get("cmd") == "set":
                store.set_param(
                    msg.get("name"),
                    msg.get("O"),
                    msg.get("step"),
                    msg.get("reversion"),
                )
    except OSError:
        pass
    finally:
        stop.set()


def handle_client(conn, addr, store):
    print(f"Slave connected: {addr}")
    stop = threading.Event()
    t = threading.Thread(target=reader_thread,
                         args=(conn, store, stop), daemon=True)
    t.start()

    t0 = time.monotonic()
    period = 1.0 / SEND_HZ
    try:
        while not stop.is_set():
            now = time.monotonic() - t0
            for name in SIGNAL_NAMES:                  # 5 messages, one signal each
                msg = {"name": name,
                       "value": store.value(name, now),
                       "t": now}
                conn.sendall((json.dumps(msg) + "\n").encode("utf-8"))
            time.sleep(period)
    except OSError:
        pass
    finally:
        stop.set()
        conn.close()
        print(f"Slave disconnected: {addr}")


def main():
    store = SignalStore(INITIAL_PARAMS)
    srv = socket.socket(socket.AF_INET, socket.SOCK_STREAM)
    srv.setsockopt(socket.SOL_SOCKET, socket.SO_REUSEADDR, 1)
    srv.bind((HOST, PORT))
    srv.listen(1)
    print(f"Master listening on {HOST}:{PORT}")
    try:
        while True:
            conn, addr = srv.accept()
            # serial handling of one slave at a time; wrap in a thread for many
            handle_client(conn, addr, store)
    except KeyboardInterrupt:
        pass
    finally:
        srv.close()


if __name__ == "__main__":
    main()
