using System.Globalization;
using Qenex.QSuite.LogSystems.LogSystem;
using Qenex.QSuite.Protocols.Modbus;
using Qenex.QSuite.Protocols.Protocol;

namespace Qenex.QSuite.Protocols.ModbusSlave;

/// <summary>
/// Modbus slave configuration parsed from the protocol's RawSettings, e.g.
/// mode="rtu";unitId="1" or mode="tcp";unitId="255";respondToAnyUnit="true".
/// respondToAnyUnit defaults to true for TCP (where masters commonly send unit id 0xFF or ignore
/// it) and false for RTU (a shared bus where the unit id addresses devices).
/// </summary>
public sealed class ModbusSlaveSettings
{
    public bool IsTcp { get; init; }
    public byte UnitId { get; init; } = 1;
    public bool RespondToAnyUnit { get; init; }

    public IModbusFramer CreateFramer()
    {
        return IsTcp ? new ModbusTcpFramer() : new ModbusRtuFramer(ModbusFramerRole.Slave);
    }

    private static readonly string[] KnownSettings = ["mode", "unitId", "respondToAnyUnit"];

    public static ModbusSlaveSettings Parse(string rawSettings, ILogger? logger = null)
    {
        var settings = SettingsParser.Parse(rawSettings, KnownSettings, logger, "Modbus slave protocol");

        var isTcp = ParseMode(settings);
        return new ModbusSlaveSettings
        {
            IsTcp = isTcp,
            UnitId = ParseUnitId(settings),
            RespondToAnyUnit = ParseRespondToAnyUnit(settings, defaultValue: isTcp)
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

    private static byte ParseUnitId(IReadOnlyDictionary<string, string> settings)
    {
        if (!settings.TryGetValue("unitId", out var value))
        {
            return 1;
        }

        return byte.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var unitId)
            ? unitId
            : throw new ArgumentException($"Invalid setting unitId='{value}'.");
    }

    private static bool ParseRespondToAnyUnit(IReadOnlyDictionary<string, string> settings, bool defaultValue)
    {
        if (!settings.TryGetValue("respondToAnyUnit", out var value))
        {
            return defaultValue;
        }

        return bool.TryParse(value, out var parsed)
            ? parsed
            : throw new ArgumentException($"Invalid setting respondToAnyUnit='{value}' (expected true/false).");
    }
}
