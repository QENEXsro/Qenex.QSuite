using System.Globalization;
using Qenex.QSuite.Protocols.Protocol;

namespace Qenex.QSuite.Protocols.RawCanProtocol;

/// <summary>Byte order used when assembling a multi-byte value from CAN frame bytes.</summary>
public enum RawCanByteOrder { LittleEndian, BigEndian }

/// <summary>
/// Addressing of one variable on the CAN bus: the frame identifier, where in the frame the value
/// sits (byte offset, byte order) and the direction. A read variable is updated from every received
/// frame with that id; a write variable is sent as a frame with that id when it is written.
/// </summary>
public class RawCanProtocolVariableSpecification : ProtVariableSpecification
{
    private const uint MaxStandardId = 0x7FF;
    private const uint MaxExtendedId = 0x1FFFFFFF;

    public RawCanProtocolVariableSpecification()
    {
        Name = "RawCanProtocolVariableSpecification";
    }

    /// <summary>CAN identifier this variable is bound to (11-bit standard or 29-bit extended).</summary>
    public uint CanId { get; init; }

    /// <summary>
    /// True for a 29-bit extended identifier. Inferred from the id (above 0x7FF) or forced with
    /// extended="true" for a 29-bit frame whose id happens to fit into 11 bits.
    /// </summary>
    public bool IsExtended { get; init; }

    /// <summary>Index of the first frame data byte the value is read from / written to (default 0).</summary>
    public int Offset { get; init; }

    /// <summary>Byte order used to assemble multi-byte values (default little-endian).</summary>
    public RawCanByteOrder ByteOrder { get; init; } = RawCanByteOrder.LittleEndian;

    public CommDirection Direction { get; init; } = CommDirection.Read;

    /// <summary>
    /// Controlled serialization so the configuration round-trips with the keys <see cref="Create"/>
    /// reads: direction, canId in hex, offset, byteOrder as le/be, extended only when it cannot be
    /// inferred from the id. The generic reflective writer would emit the properties in decimal under
    /// names the parser does not know.
    /// </summary>
    public string CommParams
    {
        get
        {
            var text = $"direction=\"{Direction.ToString().ToLowerInvariant()}\";canId=\"0x{CanId:X}\";offset=\"{Offset}\";byteOrder=\"{(ByteOrder == RawCanByteOrder.BigEndian ? "be" : "le")}\"";
            return IsExtended && CanId <= MaxStandardId ? text + ";extended=\"true\"" : text;
        }
    }

    /// <summary>
    /// Parses direction="read";canId="0x100";offset="0";byteOrder="le" (offset, byteOrder, direction
    /// and extended are optional). A missing or unreadable value throws, so the variable is reported
    /// and left out instead of silently listening on id 0.
    /// </summary>
    public static RawCanProtocolVariableSpecification Create(string commParams)
    {
        var settings = Parse(commParams);

        if (!settings.TryGetValue("canId", out var idText) || string.IsNullOrWhiteSpace(idText))
        {
            throw new ArgumentException("Missing mandatory commParam 'canId' (hexadecimal, e.g. canId=\"0x100\").");
        }

        var idDigits = idText.Trim();
        if (idDigits.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
        {
            idDigits = idDigits[2..];
        }

        if (!uint.TryParse(idDigits, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var canId) || canId > MaxExtendedId)
        {
            throw new ArgumentException($"Invalid commParam canId=\"{idText}\" (expected a hexadecimal identifier up to 0x1FFFFFFF).");
        }

        var extended = canId > MaxStandardId;
        if (settings.TryGetValue("extended", out var extendedText) && !string.IsNullOrWhiteSpace(extendedText))
        {
            if (!bool.TryParse(extendedText, out var forced))
            {
                throw new ArgumentException($"Invalid commParam extended=\"{extendedText}\" (expected true or false).");
            }

            if (!forced && canId > MaxStandardId)
            {
                throw new ArgumentException($"canId=\"{idText}\" does not fit into an 11-bit standard identifier; drop extended=\"false\".");
            }

            extended = forced;
        }

        var offset = 0;
        if (settings.TryGetValue("offset", out var offsetText) && !string.IsNullOrWhiteSpace(offsetText))
        {
            if (!int.TryParse(offsetText, NumberStyles.Integer, CultureInfo.InvariantCulture, out offset) || offset < 0 || offset > 7)
            {
                throw new ArgumentException($"Invalid commParam offset=\"{offsetText}\" (expected 0 to 7).");
            }
        }

        var byteOrder = RawCanByteOrder.LittleEndian;
        if (settings.TryGetValue("byteOrder", out var orderText) && !string.IsNullOrWhiteSpace(orderText))
        {
            byteOrder = orderText.Trim().ToLowerInvariant() switch
            {
                "le" => RawCanByteOrder.LittleEndian,
                "be" => RawCanByteOrder.BigEndian,
                _ => throw new ArgumentException($"Invalid commParam byteOrder=\"{orderText}\" (expected le or be).")
            };
        }

        var direction = CommDirection.Read;
        if (settings.TryGetValue("direction", out var directionText) && !string.IsNullOrWhiteSpace(directionText))
        {
            if (!Enum.TryParse<CommDirection>(directionText, ignoreCase: true, out direction))
            {
                throw new ArgumentException($"Invalid commParam direction=\"{directionText}\" (expected read, write or readWrite).");
            }
        }

        return new RawCanProtocolVariableSpecification
        {
            CanId = canId,
            IsExtended = extended,
            Offset = offset,
            ByteOrder = byteOrder,
            Direction = direction
        };
    }

    private static Dictionary<string, string> Parse(string commParams)
    {
        return commParams
            .Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(item => item.Split('=', 2, StringSplitOptions.TrimEntries))
            .Where(parts => parts.Length == 2)
            .ToDictionary(parts => parts[0], parts => parts[1].Trim('"'), StringComparer.OrdinalIgnoreCase);
    }
}
