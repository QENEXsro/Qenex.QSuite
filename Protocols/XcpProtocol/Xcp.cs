using System.Reflection;
using Qenex.QSuite.Common.CoreComm;
using Qenex.QSuite.Protocols.XcpCore;
using Qenex.QSuite.Specifications.Specification;

namespace Qenex.QSuite.Protocols.XcpProtocol;

/// <summary>
/// Simplified XCP master (ASAM MCD-1 XCP 1.1) over a CAN driver. The session engine, polling and
/// operator writes live in <see cref="XcpProtocolBase{TFrame}"/>; this class binds them to CAN
/// framing: commands go out on MasterID padded to DLC 8, responses come back on SlaveID.
/// </summary>
public class Xcp : XcpProtocolBase<CanFrame>
{
    private XcpSessionSettings settings = new();

    public Xcp()
    {
        Specification = new SpecificationBase
        {
            Name = "XcpProtocol",
            Label = "XCP on CAN",
            Description = "XCP master: periodic ECU memory reads and operator writes. Use with the PEAK CAN Adapter driver.",
            CreatedOn = new DateTime(2026, 7, 10),
            Version = Assembly.GetExecutingAssembly().GetName().Version ?? new Version(1, 0, 0),
            Author = "Qenex",
            Company = "QENEX Ltd."
        };
    }

    // masterId/slaveId are the CAN identifiers (hex, 0x prefix optional) and must differ — the
    // 0x200/0x201 values are placeholders the operator replaces with the slave's actual ids.
    public override string DefaultRawSettings => "masterId=0x200;slaveId=0x201;extendedIds=false;requestTimeoutMs=1000;daqTimestamps=slave";

    protected override void ApplyConfiguration(string rawSettings)
    {
        settings = XcpSessionSettings.Parse(rawSettings, Logger);
    }

    protected override int SessionTimeoutMs => settings.TimeoutMs;

    protected override bool UseSlaveDaqTimestamps => settings.UseSlaveDaqTimestamps;

    protected override string TransportName => "CAN";

    protected override string SlaveDescription => $"XCP slave 0x{settings.SlaveId:X}";

    protected override Func<byte[], CancellationToken, Task> CreatePacketTransmitter(
        Func<CanFrame, CancellationToken, Task> frameTransmitter)
    {
        // CAN framing: the XCP packet goes directly into the data field of a frame with the
        // configured master id; outgoing DLC is always padded to 8 bytes.
        return (packet, token) => frameTransmitter(
            new CanFrame(settings.MasterId, PadToClassicCanDlc(packet), settings.IsExtendedId), token);
    }

    public override Task AddReceivedDataToQueueAsync(IEnumerable<CanFrame> data, CancellationToken ct = default)
    {
        var session = Master;
        if (session == null)
        {
            return Task.CompletedTask;
        }

        foreach (var frame in data)
        {
            // Only the configured slave id carries XCP responses for this session; everything else
            // on the bus is other traffic and is ignored.
            if (frame.CanId == settings.SlaveId && frame.IsExtended == settings.IsExtendedId && frame.Data.Length > 0)
            {
                session.OnPacketReceived(frame.Data);
            }
        }

        return Task.CompletedTask;
    }

    private static byte[] PadToClassicCanDlc(byte[] packet)
    {
        if (packet.Length >= 8)
        {
            return packet;
        }

        var padded = new byte[8];
        packet.CopyTo(padded, 0);
        return padded;
    }
}
