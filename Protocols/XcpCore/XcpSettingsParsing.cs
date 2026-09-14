namespace Qenex.QSuite.Protocols.XcpCore;

/// <summary>Parsing of XCP protocol settings shared by all transports (CAN, TCP).</summary>
public static class XcpSettingsParsing
{
    public const int DefaultRequestRetries = 2;

    /// <summary>
    /// Parses the optional requestRetries setting: how many times a command is repeated after a
    /// response timeout (with a SYNCH re-synchronization in between), on top of the first attempt.
    /// Default 2 = up to three transmissions, the same meaning as the Modbus master setting.
    /// </summary>
    public static int ParseRequestRetries(IReadOnlyDictionary<string, string> settings)
    {
        if (!settings.TryGetValue("requestRetries", out var value))
        {
            return DefaultRequestRetries;
        }

        return int.TryParse(value, out var retries) && retries >= 0
            ? retries
            : throw new ArgumentException($"Invalid setting requestRetries='{value}' (expected 0 or a positive integer).");
    }

    /// <summary>
    /// Parses the optional daqTimestamps setting: "slave" (default) puts DAQ samples onto the
    /// time axis of the ECU clock, "master" stamps them with the PC receive time. Maps onto the
    /// standard SET_DAQ_LIST_MODE timestamp bit: with "master" the bit stays clear and a QFW SDK
    /// slave transfers no timestamps (full ODT 0 capacity); a legacy TIMESTAMP_FIXED slave still
    /// sends them and they are ignored.
    /// </summary>
    public static bool ParseUseSlaveDaqTimestamps(IReadOnlyDictionary<string, string> settings)
    {
        if (!settings.TryGetValue("daqTimestamps", out var value))
        {
            return true;
        }

        return value.ToLowerInvariant() switch
        {
            "slave" => true,
            "master" => false,
            _ => throw new ArgumentException($"Invalid setting daqTimestamps='{value}' (expected slave or master).")
        };
    }
}
