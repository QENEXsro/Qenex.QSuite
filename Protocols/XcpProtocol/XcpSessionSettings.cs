using System.Globalization;
using Qenex.QSuite.LogSystems.LogSystem;
using Qenex.QSuite.Protocols.Protocol;
using Qenex.QSuite.Protocols.XcpCore;

namespace Qenex.QSuite.Protocols.XcpProtocol;

/// <summary>
/// XCP session configuration parsed from the protocol's RawSettings, e.g.
/// masterId="0x200";slaveId="0x201";extendedIds="false";requestTimeoutMs="1000";requestRetries="2";daqTimestamps="slave".
/// Both CAN identifiers are entered in hexadecimal (0x prefix optional), matching the repo
/// convention for CAN ids. Byte order and address granularity are NOT configured — they come
/// from the slave's CONNECT response.
/// </summary>
public sealed class XcpSessionSettings
{
    /// <summary>CAN identifier for master → slave command frames (TX).</summary>
    public uint MasterId { get; init; }

    /// <summary>CAN identifier for slave → master response frames (RX).</summary>
    public uint SlaveId { get; init; }

    /// <summary>True when both identifiers are 29-bit extended ids.</summary>
    public bool IsExtendedId { get; init; }

    /// <summary>Response timeout per command (EV_CMD_PENDING restarts it).</summary>
    public int TimeoutMs { get; init; } = 1000;

    /// <summary>Command repetitions after a timeout, on top of the first attempt.</summary>
    public int RequestRetries { get; init; } = XcpSettingsParsing.DefaultRequestRetries;

    /// <summary>DAQ time axis source: true = ECU timestamps (default), false = PC receive time.</summary>
    public bool UseSlaveDaqTimestamps { get; init; } = true;

    private static readonly string[] KnownSettings = ["masterId", "slaveId", "extendedIds", "requestTimeoutMs", "requestRetries", "daqTimestamps"];

    public static XcpSessionSettings Parse(string rawSettings, ILogger? logger = null)
    {
        var settings = SettingsParser.Parse(rawSettings, KnownSettings, logger, "XCP on CAN protocol");

        var masterId = ParseCanId(settings, "masterId");
        var slaveId = ParseCanId(settings, "slaveId");
        var isExtended = ParseBool(settings, "extendedIds");
        var timeoutMs = ParseTimeout(settings);

        if (masterId == slaveId)
        {
            throw new ArgumentException("masterId and slaveId must differ.");
        }

        var maxId = isExtended ? 0x1FFFFFFFu : 0x7FFu;
        if (masterId > maxId || slaveId > maxId)
        {
            throw new ArgumentException(
                $"CAN id out of range for {(isExtended ? "29-bit extended" : "11-bit standard")} identifiers (max 0x{maxId:X}).");
        }

        return new XcpSessionSettings
        {
            MasterId = masterId,
            SlaveId = slaveId,
            IsExtendedId = isExtended,
            TimeoutMs = timeoutMs,
            RequestRetries = XcpSettingsParsing.ParseRequestRetries(settings),
            UseSlaveDaqTimestamps = XcpSettingsParsing.ParseUseSlaveDaqTimestamps(settings)
        };
    }

    private static uint ParseCanId(IReadOnlyDictionary<string, string> settings, string key)
    {
        if (!settings.TryGetValue(key, out var value) || string.IsNullOrWhiteSpace(value))
        {
            throw new ArgumentException($"Missing mandatory setting '{key}'.");
        }

        var text = value.Trim();
        if (text.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
        {
            text = text[2..];
        }

        return uint.TryParse(text, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var id)
            ? id
            : throw new ArgumentException($"Invalid setting {key}='{value}' (expected a hexadecimal CAN id).");
    }

    private static bool ParseBool(IReadOnlyDictionary<string, string> settings, string key)
    {
        if (!settings.TryGetValue(key, out var value))
        {
            return false;
        }

        return bool.TryParse(value, out var parsed)
            ? parsed
            : throw new ArgumentException($"Invalid setting {key}='{value}' (expected true/false).");
    }

    private static int ParseTimeout(IReadOnlyDictionary<string, string> settings)
    {
        if (!settings.TryGetValue("requestTimeoutMs", out var value))
        {
            return 1000;
        }

        return int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var timeout) && timeout > 0
            ? timeout
            : throw new ArgumentException($"Invalid setting requestTimeoutMs='{value}' (expected a positive integer).");
    }
}
