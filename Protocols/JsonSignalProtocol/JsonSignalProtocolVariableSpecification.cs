using Qenex.QSuite.Protocols.Protocol;

namespace Qenex.QSuite.Protocols.JsonSignalProtocol;

/// <summary>
/// Addressing of one JSON signal variable: the signal name carried in the "name" field of the
/// messages and the direction (read = the device sends the signal, write / readWrite = QInsight
/// sends it). The properties match the commParam keys 1:1 ("name", "direction"), so the
/// inherited reflection-based ToCommParam() writes the project back with the same keys.
/// </summary>
public class JsonSignalProtocolVariableSpecification : ProtVariableSpecification
{
    public JsonSignalProtocolVariableSpecification()
    {
        Name = "JsonSignalProtocolVariableSpecification";
    }

    /// <summary>Signal name as it appears in the "name" field; defaults to the variable name.</summary>
    public string SignalName { get; init; } = string.Empty;

    public CommDirection Direction { get; init; } = CommDirection.Read;

    public static JsonSignalProtocolVariableSpecification Create(string commParams, string variableName)
    {
        var settings = ParseSettings(commParams);

        // Only "name" is read; unknown keys (including the aliases of older projects) are ignored.
        var signalName = settings.TryGetValue("name", out var name) && !string.IsNullOrWhiteSpace(name)
            ? name
            : variableName;

        var direction = CommDirection.Read;
        if (settings.TryGetValue("direction", out var directionText)
            && !string.IsNullOrWhiteSpace(directionText)
            && Enum.TryParse<CommDirection>(directionText, ignoreCase: true, out var parsedDirection))
        {
            direction = parsedDirection;
        }

        return new JsonSignalProtocolVariableSpecification
        {
            SignalName = signalName,
            Direction = direction
        };
    }

    // The base writer emits every public property with its camelCase name: "name" from
    // SignalName would come out as "signalName", so the two keys are written explicitly.
    public override string ToCommParam()
    {
        return $"direction=\"{Direction.ToString().ToLowerInvariant()}\";name=\"{SignalName}\"";
    }

    private static Dictionary<string, string> ParseSettings(string rawSettings)
    {
        var settings = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var parts in rawSettings
                     .Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                     .Select(item => item.Split('=', 2, StringSplitOptions.TrimEntries))
                     .Where(parts => parts.Length == 2))
        {
            settings[parts[0]] = parts[1].Trim('"');
        }

        return settings;
    }
}
