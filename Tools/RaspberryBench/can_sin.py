import socket, struct, math, time

s = socket.socket(socket.AF_CAN, socket.SOCK_RAW, socket.CAN_RAW)
s.bind(('can0',))

can_id = 0x100
f = 1.0
period = 0.1
t = 0.0
next_t = time.monotonic()

while True:
    val = 2.0 * math.sin(2*math.pi*f*t) 
    data = struct.pack('<f', val)
    # SocketCAN rámec: <ID(4) DLC(1) pad(3) data(8)>
    frame = struct.pack("=IB3x8s", can_id, len(data), data)
    s.send(frame)
    t += period
    next_t += period
    time.sleep(max(0, next_t - time.monotonic()))