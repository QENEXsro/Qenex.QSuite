import socket
import threading
import json
import math
import time
import random

HOST = "0.0.0.0"
PORT = 5000
SEND_HZ = 200

# Per-signal parameters for a stepped (staircase) signal:
#   A          = výška jednoho schodu (amplituda kvanta)
#   O          = offset (svislé posunutí celého signálu)
#   step_time  = jak dlouho [s] drží jedna úroveň, než skočí na další
#   levels     = počet různých úrovní, mezi kterými signál cyklicky přeskakuje
#   noise      = amplituda náhodného šumu superponovaného na úroveň
INITIAL_PARAMS = {
    "step1": {"A": 10.0, "O":   0.0, "step_time": 1.0, "levels": 6, "noise": 1.5},
    "step2": {"A": 20.0, "O":  -5.0, "step_time": 0.7, "levels": 5, "noise": 2.0},
    "step3": {"A":  5.0, "O":   8.0, "step_time": 1.5, "levels": 8, "noise": 0.8},
    "step4": {"A": 30.0, "O":   0.5, "step_time": 0.5, "levels": 4, "noise": 3.0},
    "step5": {"A": 15.0, "O":   2.0, "step_time": 1.2, "levels": 7, "noise": 1.2},
}
SIGNAL_NAMES = list(INITIAL_PARAMS.keys())


class SignalStore:
    """Thread-safe store of per-signal parameters."""

    def __init__(self, initial):
        self._lock = threading.Lock()
        self._sig = {n: dict(p) for n, p in initial.items()}

    def value(self, name, t):
        with self._lock:
            p = self._sig[name]
            A         = p["A"]
            O         = p["O"]
            step_time = p["step_time"]
            levels    = int(p["levels"])
            noise     = p["noise"]

        # Index aktuálního schodu (kolikátý časový úsek běží).
        idx = int(t / step_time)
        # Pseudo-náhodná, ale pro daný idx STABILNÍ úroveň (0 .. levels-1),
        # aby schod po celou dobu step_time držel stejnou hodnotu.
        level = (idx * 2654435761) % levels        # Knuth multiplicative hash
        base = O + A * level
        # Šum se mění každý vzorek -> "chlupatá" čára jako na obrázku.
        return base + random.uniform(-noise, noise)

    def set_param(self, name, A=None, O=None, step_time=None,
                  levels=None, noise=None):
        with self._lock:
            if name not in self._sig:
                return False
            if A is not None:
                self._sig[name]["A"] = float(A)
            if O is not None:
                self._sig[name]["O"] = float(O)          # opraveno (bylo float(A))
            if step_time is not None:
                self._sig[name]["step_time"] = float(step_time)
            if levels is not None:
                self._sig[name]["levels"] = int(levels)
            if noise is not None:
                self._sig[name]["noise"] = float(noise)
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
                    msg.get("A"),
		    msg.get("O"),
                    msg.get("f"),
                    msg.get("phi"),
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