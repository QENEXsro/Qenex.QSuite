using System.Globalization;
using Qenex.QSuite.LogSystems.LogSystem;
using Qenex.QSuite.Protocols.Protocol;
using Qenex.QSuite.Protocols.XcpCore;

namespace Qenex.QSuite.Protocols.XcpTcpProtocol;

/// <summary>
/// XCP on TCP session configuration parsed from the protocol's RawSettings, e.g.
/// requestTimeoutMs="1000";daqTimestamps="slave". Host and port belong to the TCP Client driver, and
/// byte order comes from the slave's CONNECT response. Empty settings are valid (all defaults).
/// </summary>
public sealed class XcpTcpSessionSettings
{
    /// <summary>Response timeout per command (EV_CMD_PENDING restarts it).</summary>
    public int TimeoutMs { get; init; } = 1000;

    /// <summary>DAQ time axis source: true = ECU timestamps (default), false = PC receive time.</summary>
    public bool UseSlaveDaqTimestamps { get; init; } = true;

    private static readonly string[] KnownSettings = ["requestTimeoutMs", "daqTimestamps"];

    public static XcpTcpSessionSettings Parse(string rawSettings, ILogger? logger = null)
    {
        var settings = SettingsParser.Parse(rawSettings, KnownSettings, logger, "XCP on TCP protocol");

        return new XcpTcpSessionSettings
        {
            TimeoutMs = ParseTimeout(settings),
            UseSlaveDaqTimestamps = XcpSettingsParsing.ParseUseSlaveDaqTimestamps(settings)
        };
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
