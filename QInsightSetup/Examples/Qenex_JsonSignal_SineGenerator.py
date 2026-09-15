#!/usr/bin/env python3
"""Sine signal generator - the counterpart of QInsight's JSON Signal Stream protocol.

Shipped with QInsight in the Examples folder; used by the examples
Qenex_TcpClient_JsonSignal_PythonSines.qproj and Qenex_Serial_JsonSignal_PythonSines.qproj.

Runs as a TCP server (default, QInsight connects with the TCP Client driver) or on a
serial port (QInsight connects with the Serial Port driver):

    python Qenex_JsonSignal_SineGenerator.py                  # TCP server on 0.0.0.0:5000
    python Qenex_JsonSignal_SineGenerator.py --port 6000      # TCP server on another port
    python Qenex_JsonSignal_SineGenerator.py --serial COM7    # serial port, 115200 baud
    python Qenex_JsonSignal_SineGenerator.py --serial /dev/ttyUSB0 --baud 57600

The generator continuously sends 5 messages (one signal each), where each signal value
is O + A * sin(2*pi*f*t + phi). It also listens for lines that change the offset (O),
amplitude (A), frequency (f) or phase (phi) of any signal at runtime.

Wire protocol (newline-delimited JSON, the same shape in both directions):
  Generator -> QInsight: {"name": "sine1", "value": 0.7071, "t": 0.05}\\n   (5x per cycle)
  QInsight -> generator: {"name": "sine1_A", "value": 2.0}\\n
                         The name is "<signal>_<parameter>" with parameter A (amplitude),
                         O (offset), f (frequency [Hz]) or phi (phase [rad]); one line
                         changes one parameter of one signal. This is exactly what the
                         JSON Signal Stream protocol sends for a variable with
                         direction="write" and name="sine1_A".

Serial mode needs pyserial (pip install pyserial).
"""
import argparse
import json
import math
import socket
import threading
import time

SEND_HZ = 20            # how often a full set of samples is sent, per second

# Per-signal initial parameters.
#   A   = amplitude
#   O   = offset
#   f   = frequency [Hz]
#   phi = phase offset [rad]
# The send order follows the order of keys below (dict preserves insertion order).
INITIAL_PARAMS = {
    "sine1": {"A": 10.0, "O": 2.0,  "f": 1.0,  "phi": 0.0},
    "sine2": {"A": 20.0, "O": -5.0, "f": 0.5,  "phi": math.pi / 4},
    "sine3": {"A": 5.0,  "O": 8.0,  "f": 10.0, "phi": math.pi / 2},
    "sine4": {"A": 30.0, "O": 0.5,  "f": 0.25, "phi": math.pi},
    "sine5": {"A": 15.0, "O": 2.0,  "f": 2.0,  "phi": 3 * math.pi / 2},
}
SIGNAL_NAMES = list(INITIAL_PARAMS.keys())
PARAM_NAMES = ("A", "O", "f", "phi")


class SignalStore:
    """Thread-safe store of per-signal parameters."""

    def __init__(self, initial):
        self._lock = threading.Lock()
        # deep-ish copy so INITIAL_PARAMS is not mutated by runtime commands
        self._sig = {n: dict(p) for n, p in initial.items()}

    def value(self, name, t):
        with self._lock:
            p = self._sig[name]
            A, O, f, phi = p["A"], p["O"], p["f"], p["phi"]
        return O + A * math.sin(2.0 * math.pi * f * t + phi)

    def set_param(self, name, param, value):
        with self._lock:
            if name not in self._sig or param not in PARAM_NAMES:
                return False
            self._sig[name][param] = float(value)
            return True


def apply_line(store, raw):
    """One incoming line: {"name": "sine1_A", "value": 2.0} -> sine1.A = 2.0."""
    line = raw.strip()
    if not line:
        return
    try:
        msg = json.loads(line.decode("utf-8"))
    except (json.JSONDecodeError, UnicodeDecodeError):
        return
    name, value = msg.get("name"), msg.get("value")
    if not isinstance(name, str) or "_" not in name or value is None:
        return
    signal, param = name.rsplit("_", 1)
    if store.set_param(signal, param, value):
        print(f"{signal}.{param} = {value}", flush=True)


def reader_thread(readable, store, stop):
    """Listen for lines from QInsight and apply parameter changes."""
    try:
        for raw in readable:                 # read line by line (socket file / serial port)
            if stop.is_set():
                break
            apply_line(store, raw)
    except (OSError, ValueError):
        pass
    finally:
        stop.set()


def send_loop(write, store, stop):
    """Send all signals SEND_HZ times per second until stop is set or the link breaks."""
    t0 = time.monotonic()
    period = 1.0 / SEND_HZ
    next_t = time.monotonic()
    try:
        while not stop.is_set():
            now = time.monotonic() - t0
            for name in SIGNAL_NAMES:                  # 5 messages, one signal each
                msg = {"name": name, "value": store.value(name, now), "t": round(now, 4)}
                write((json.dumps(msg) + "\n").encode("utf-8"))
            next_t += period
            time.sleep(max(0.0, next_t - time.monotonic()))
    except OSError:
        pass
    except KeyboardInterrupt:
        stop.set()
        raise
    finally:
        stop.set()


def serve_tcp(host, port, store):
    srv = socket.socket(socket.AF_INET, socket.SOCK_STREAM)
    srv.setsockopt(socket.SOL_SOCKET, socket.SO_REUSEADDR, 1)
    srv.bind((host, port))
    srv.listen(1)
    # Windows delivers Ctrl+C to the main thread only when it is not stuck in a blocking
    # socket call, so accept() polls with a timeout instead of blocking forever.
    srv.settimeout(0.5)
    print(f"Generator listening on {host}:{port} - connect QInsight with the TCP Client driver (Ctrl+C to stop)", flush=True)
    try:
        while True:
            try:
                conn, addr = srv.accept()
            except socket.timeout:
                continue
            conn.settimeout(None)
            print(f"QInsight connected: {addr}", flush=True)
            stop = threading.Event()
            threading.Thread(target=reader_thread, args=(conn.makefile("rb"), store, stop), daemon=True).start()
            send_loop(conn.sendall, store, stop)
            conn.close()
            print(f"QInsight disconnected: {addr}")
    except KeyboardInterrupt:
        pass
    finally:
        srv.close()


def serve_serial(port, baud, store):
    import serial  # pyserial
    ser = serial.Serial(port, baud, timeout=1)
    print(f"Generator on {port} @ {baud} baud - connect QInsight with the Serial Port driver")
    stop = threading.Event()

    def lines():
        while not stop.is_set():
            raw = ser.readline()
            if raw:
                yield raw

    threading.Thread(target=reader_thread, args=(lines(), store, stop), daemon=True).start()
    try:
        send_loop(ser.write, store, stop)
    except KeyboardInterrupt:
        pass
    finally:
        stop.set()
        ser.close()


def main():
    parser = argparse.ArgumentParser(description="JSON Signal Stream sine generator (TCP server or serial port).")
    parser.add_argument("--host", default="0.0.0.0", help="TCP bind address (default 0.0.0.0)")
    parser.add_argument("--port", type=int, default=5000, help="TCP port (default 5000)")
    parser.add_argument("--serial", metavar="PORT", help="use a serial port instead of TCP, e.g. COM7 or /dev/ttyUSB0")
    parser.add_argument("--baud", type=int, default=115200, help="serial baud rate (default 115200)")
    args = parser.parse_args()

    store = SignalStore(INITIAL_PARAMS)
    if args.serial:
        serve_serial(args.serial, args.baud, store)
    else:
        serve_tcp(args.host, args.port, store)


if __name__ == "__main__":
    main()
