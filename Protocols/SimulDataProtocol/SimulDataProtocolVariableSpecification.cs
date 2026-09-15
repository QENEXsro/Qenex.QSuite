using Qenex.QSuite.Protocols.Protocol;
using Qenex.QSuite.Variables.VariableEvents;

namespace Qenex.QSuite.Protocols.SimulDataProtocol;

public class SimulDataProtocolVariableSpecification : ProtVariableSpecification
{
    public SimulDataProtocolVariableSpecification()
    {
        Name = "SimulDataProtocolVariableSpecification";
    }

    public IVarEvent? VariableEvent { get; set; }

    /// <summary>Signal catalog key (see <see cref="SimulSignalCatalog"/>); empty = fallback by value type.</summary>
    public string Signal { get; set; } = string.Empty;

    /// <summary>
    /// Optional generator overrides (amp=, freq=, nonlin=); null = signal default. The property
    /// names equal the commParam keys on purpose: the inherited reflection-based ToCommParam writes
    /// the project back with property names, so a mismatch would lose the values on the next load.
    /// </summary>
    public double? Amp { get; set; }
    public double? Freq { get; set; }
    public double? Nonlin { get; set; }

    /// <summary>
    /// Optional initial value (init=) of a writable parameter variable: the protocol writes it
    /// into the variable at every start (the simulated device's power-on default) and notifies,
    /// so controls show it. Null = the variable keeps whatever value it has.
    /// </summary>
    public double? Init { get; set; }

    public CommDirection Direction { get; set; } = CommDirection.Read;

    public static SimulDataProtocolVariableSpecification CreateDefault(IVarEvent? variableEvent)
    {
        return new SimulDataProtocolVariableSpecification
        {
            VariableEvent = variableEvent,
            Direction = CommDirection.Read
        };
    }

    public static SimulDataProtocolVariableSpecification Create(IVarEvent? variableEvent, string commParams)
    {
        var parameters = ParseCommParams(commParams);

        // Tolerant on purpose: direction is informative only for a generator, so a typo must
        // not knock the variable out of communication.
        var direction = CommDirection.Read;
        if (parameters.TryGetValue("direction", out var directionStr)
            && !string.IsNullOrWhiteSpace(directionStr)
            && Enum.TryParse<CommDirection>(directionStr, ignoreCase: true, out var parsedDirection))
        {
            direction = parsedDirection;
        }

        return new SimulDataProtocolVariableSpecification
        {
            VariableEvent = variableEvent,
            Direction = direction,
            Signal = parameters.GetValueOrDefault("signal", string.Empty).ToLowerInvariant(),
            Amp = ParseDouble(parameters, "amp"),
            Freq = ParseDouble(parameters, "freq"),
            Nonlin = ParseDouble(parameters, "nonlin"),
            Init = ParseDouble(parameters, "init")
        };
    }

    // Tolerant like the direction parsing: an unparsable number falls back to the signal
    // default instead of knocking the variable out of communication.
    private static double? ParseDouble(Dictionary<string, string> parameters, string key)
    {
        return parameters.TryGetValue(key, out var text)
               && double.TryParse(text, System.Globalization.NumberStyles.Float,
                   System.Globalization.CultureInfo.InvariantCulture, out var value)
            ? value
            : null;
    }

    private static Dictionary<string, string> ParseCommParams(string commParams)
    {
        var parameters = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var parts in commParams
                     .Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                     .Select(parameter => parameter.Split('=', 2))
                     .Where(parts => parts.Length == 2))
        {
            parameters[parts[0].Trim()] = parts[1].Trim().Trim('"');
        }

        return parameters;
    }
}
