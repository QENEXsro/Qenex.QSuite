# Raspberry bench scripts

Signal generators and systemd units for the Raspberry Pi test bench (Pi Zero W with a CAN hat,
Pi with Ethernet). They are the counterparts of QInsight drivers and protocols:

| Script | Transport | QInsight side |
|---|---|---|
| `rpi01_tcp_master_rnd.py` | TCP server :5000, 5 random walks, JSON lines | TCP Client + JSON Signal Stream |
| `zero02_tcp_master_step.py` | TCP server :5000, 5 stepped signals, JSON lines | TCP Client + JSON Signal Stream |
| `can_sin.py` | SocketCAN can0, one sine on 0x100, float32 | PEAK CAN + Raw CAN Frames |
| `*.service` | systemd units starting the scripts (and can0) at boot | |

The CAN random-walk generator (ids 0x100-0x104, accepts offset/step writes on 0x180-0x194) ships with
QInsight as `QInsightSetup/Examples/Qenex_RawCan_RandomWalkGenerator.py`; `zero01_can.service` starts it
(update the path there). The sine generator with the bidirectional JSON format (writes from QInsight change amplitude,
offset, frequency and phase) ships with QInsight as `QInsightSetup/Examples/Qenex_JsonSignal_SineGenerator.py`
and runs on a PC as a TCP server or on a serial port. The two generators here still use the older
`{"cmd": "set", ...}` command format.
