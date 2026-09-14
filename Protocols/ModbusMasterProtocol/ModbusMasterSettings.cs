using System.Globalization;
using Qenex.QSuite.LogSystems.LogSystem;
using Qenex.QSuite.Protocols.Modbus;
using Qenex.QSuite.Protocols.Protocol;

namespace Qenex.QSuite.Protocols.ModbusMaster;

/// <summary>
/// Modbus master session configuration parsed from the protocol's RawSettings, e.g.
/// mode="rtu";unitId="1";requestTimeoutMs="1000";requestRetries="2". The mode selects the framing —
/// "rtu" for serial lines (CRC16), "tcp" for Modbus TCP (MBAP header) — matching the
/// driver the protocol is hosted on.
/// </summary>
public sealed class ModbusMasterSettings
{
    public bool IsTcp { get; init; }
    public byte UnitId { get; init; } = 1;
    public int TimeoutMs { get; init; } = 1000;
    public int Retries { get; init; } = 2;

    public IModbusFramer CreateFramer()
    {
        return IsTcp ? new ModbusTcpFramer() : new ModbusRtuFramer(ModbusFramerRole.Master);
    }

    private static readonly string[] KnownSettings = ["mode", "unitId", "requestTimeoutMs", "requestRetries"];

    public static ModbusMasterSettings Parse(string rawSettings, ILogger? logger = null)
    {
        var settings = SettingsParser.Parse(rawSettings, KnownSettings, logger, "Modbus master protocol");

        return new ModbusMasterSettings
        {
            IsTcp = ParseMode(settings),
            UnitId = ParseByte(settings, "unitId", 1),
            TimeoutMs = ParsePositiveInt(settings, "requestTimeoutMs", 1000),
            Retries = ParseNonNegativeInt(settings, "requestRetries", 2)
        };
    }

    private static bool ParseMode(IReadOnlyDictionary<string, string> settings)
    {
        if (!settings.TryGetValue("mode", out var value))
        {
            throw new ArgumentException("Missing mandatory setting 'mode' (rtu or tcp).");
        }

        return value.Trim().ToLowerInvariant() switch
        {
            "tcp" => true,
            "rtu" => false,
            _ => throw new ArgumentException($"Invalid setting mode='{value}' (expected rtu or tcp).")
        };
    }

    private static byte ParseByte(IReadOnlyDictionary<string, string> settings, string key, byte defaultValue)
    {
        if (!settings.TryGetValue(key, out var value))
        {
            return defaultValue;
        }

        return byte.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed)
            ? parsed
            : throw new ArgumentException($"Invalid setting {key}='{value}'.");
    }

    private static int ParsePositiveInt(IReadOnlyDictionary<string, string> settings, string key, int defaultValue)
    {
        if (!settings.TryGetValue(key, out var value))
        {
            return defaultValue;
        }

        return int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) && parsed > 0
            ? parsed
            : throw new ArgumentException($"Invalid setting {key}='{value}' (expected a positive integer).");
    }

    private static int ParseNonNegativeInt(IReadOnlyDictionary<string, string> settings, string key, int defaultValue)
    {
        if (!settings.TryGetValue(key, out var value))
        {
            return defaultValue;
        }

        return int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) && parsed >= 0
            ? parsed
            : throw new ArgumentException($"Invalid setting {key}='{value}' (expected a non-negative integer).");
    }
}
