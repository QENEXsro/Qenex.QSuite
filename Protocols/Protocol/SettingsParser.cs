using Qenex.QSuite.LogSystems.LogSystem;

namespace Qenex.QSuite.Protocols.Protocol;

/// <summary>
/// Shared parser of the <c>key=value;key=value</c> settings text every driver and protocol
/// carries in RawSettings. Keys are case-insensitive, values may be quoted, whitespace around
/// keys and values is ignored, entries without '=' are skipped.
/// <para>
/// Pass the owner's known keys so a setting the owner does not read — a typo, a parameter of a
/// different plugin, or a name from an older version — is reported as a warning instead of
/// being ignored silently. The operator would otherwise run with default values without knowing.
/// Own drivers and protocols are meant to use this too, so their settings behave the same way.
/// </para>
/// </summary>
public static class SettingsParser
{
    public static Dictionary<string, string> Parse(string rawSettings)
    {
        return rawSettings
            .Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(item => item.Split('=', 2, StringSplitOptions.TrimEntries))
            .Where(parts => parts.Length == 2)
            .ToDictionary(parts => parts[0], parts => parts[1].Trim('"'), StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Parses the text and logs one warning listing every key that is not in
    /// <paramref name="knownKeys"/>, together with the keys the owner does read.
    /// </summary>
    public static Dictionary<string, string> Parse(
        string rawSettings,
        IReadOnlyCollection<string> knownKeys,
        ILogger? logger,
        string ownerName)
    {
        var settings = Parse(rawSettings);
        var unknownKeys = settings.Keys
            .Where(key => !knownKeys.Contains(key, StringComparer.OrdinalIgnoreCase))
            .ToList();

        if (unknownKeys.Count > 0)
        {
            var known = knownKeys.Count > 0
                ? $"Known settings: {string.Join(", ", knownKeys)}."
                : "This plugin has no settings.";
            logger?.Log(LogLevel.Warn,
                $"{ownerName}: unknown setting(s) {string.Join(", ", unknownKeys.Select(key => $"'{key}'"))} ignored. {known}");
        }

        return settings;
    }
}
