using System.Reflection;
using Qenex.QSuite.LogSystems.LogSystem;
using Qenex.QSuite.Protocols.XcpCore;
using Qenex.QSuite.Specifications.Specification;

namespace Qenex.QSuite.Protocols.XcpTcpProtocol;

/// <summary>
/// Simplified XCP master (ASAM MCD-1 XCP 1.1) over a TCP driver. The session engine, polling and
/// operator writes live in <see cref="XcpProtocolBase{TFrame}"/>; this class binds them to the
/// XCP on Ethernet transport layer: each packet is framed with the 4-byte LEN+CTR header and the
/// received byte stream is reassembled into packets (<see cref="XcpEthernetFramer"/>). The ECU is
/// the TCP server, so the protocol pairs with the TCP Client driver, which owns host and port.
/// </summary>
public class XcpTcp : XcpProtocolBase<byte[]>
{
    private XcpTcpSessionSettings settings = new();

    // The driver's read loop and the session run loop touch the framer from different threads
    // (reassembly vs. per-session reset), and encode advances the send counter.
    private readonly XcpEthernetFramer framer = new();
    private readonly object framerLock = new();

    public XcpTcp()
    {
        Specification = new SpecificationBase
        {
            Name = "XcpTcpProtocol",
            Label = "XCP on TCP",
            Description = "XCP master: periodic ECU memory reads and operator writes over XCP on Ethernet. Use with the TCP Client driver.",
            CreatedOn = new DateTime(2026, 8, 4),
            Version = Assembly.GetExecutingAssembly().GetName().Version ?? new Version(1, 0, 0),
            Author = "Qenex",
            Company = "QENEX Ltd."
        };
    }

    public override string DefaultRawSettings => "requestTimeoutMs=1000;daqTimestamps=slave";

    // Type-compatibility alone would offer every byte[] driver (serial included); XCP on Ethernet
    // is only meaningful on the TCP client (the ECU acts as server).
    public override IReadOnlyList<string> CompatibleDrivers => ["TcpClientDriver"];

    protected override void ApplyConfiguration(string rawSettings)
    {
        settings = XcpTcpSessionSettings.Parse(rawSettings, Logger);
    }

    protected override int SessionTimeoutMs => settings.TimeoutMs;

    protected override bool UseSlaveDaqTimestamps => settings.UseSlaveDaqTimestamps;

    protected override string TransportName => "TCP";

    protected override string SlaveDescription => "XCP slave";

    protected override Func<byte[], CancellationToken, Task> CreatePacketTransmitter(
        Func<byte[], CancellationToken, Task> chunkTransmitter)
    {
        lock (framerLock)
        {
            // A fresh session starts with CTR 0 and must not inherit half-received bytes from a
            // previous connection.
            framer.Reset();
        }

        return (packet, token) =>
        {
            byte[] frame;
            lock (framerLock)
            {
                frame = framer.EncodeFrame(packet);
            }

            return chunkTransmitter(frame, token);
        };
    }

    public override Task AddReceivedDataToQueueAsync(IEnumerable<byte[]> data, CancellationToken ct = default)
    {
        var session = Master;

        foreach (var chunk in data)
        {
            // Chunks are consumed even with no session pending (stale bytes must not survive into
            // the next session); extraction happens under the lock, delivery outside it.
            var packets = new List<byte[]>();
            try
            {
                lock (framerLock)
                {
                    framer.Append(chunk);
                    while (framer.TryDequeuePacket(out var packet))
                    {
                        packets.Add(packet);
                    }
                }
            }
            catch (XcpProtocolException e)
            {
                // Corrupt framing; the framer already dropped its buffer. Pending commands time
                // out and the session layer recovers (SYNCH, retry, reconnect).
                Logger?.Log(LogLevel.Warn, $"XCP: {e.Message}");
            }

            foreach (var packet in packets)
            {
                session?.OnPacketReceived(packet);
            }
        }

        return Task.CompletedTask;
    }
}
