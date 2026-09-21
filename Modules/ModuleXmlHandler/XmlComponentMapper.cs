using System.Globalization;
using Qenex.QSuite.LogSystems.LogSystem;
using Qenex.QSuite.ModuleXmlHandler.XmlStructure;
using Qenex.QSuite.Scripting.PythonScript;
using Qenex.QSuite.Scripting.Script;
using Qenex.QSuite.Variables.ValueConversion;
using Qenex.QSuite.Variables.ValuePresentation;
using Qenex.QSuite.Variables.VariableEvents;

namespace Qenex.QSuite.ModuleXmlHandler;

/// <summary>
/// Maps presentations, conversions, variable events and scripts to/from their XML
/// representation. Shared by the module XML handler and the standalone component
/// export/import in the project configuration.
/// </summary>
public static class XmlComponentMapper
{
    #region Presentations

    public static List<XmlPresentation> ToXmlPresentations(IEnumerable<IPresentation> presentations)
    {
        return presentations.Select(presentation => new XmlPresentation
        {
            Name = presentation.Name,
            Min = presentation.Min,
            Max = presentation.Max,
            PrintFormat = presentation.PrintFormat,
            Unit = presentation.Unit,
            ConversionReference = new XmlConversionReference { Ref = presentation.Conversion?.Name ?? string.Empty }
        }).ToList();
    }

    /// <summary>
    /// A presentation whose conversion cannot be resolved is skipped (and reported) —
    /// a presentation without a conversion is not usable.
    /// </summary>
    public static List<IPresentation> FromXmlPresentations(
        IEnumerable<XmlPresentation> xmlPresentations,
        IList<IValConversion> conversions,
        ILogger? logger = null)
    {
        var presentations = new List<IPresentation>();

        foreach (var xmlPresentation in xmlPresentations)
        {
            var conversionName = xmlPresentation.ConversionReference?.Ref ?? string.Empty;
            var conversion = conversions.FirstOrDefault(c => c.Name == conversionName);
            if (conversion == null)
            {
                logger?.Log(
                    LogLevel.Error,
                    $"Presentation \"{xmlPresentation.Name}\" skipped — conversion \"{conversionName}\" not found.");
                continue;
            }

            presentations.Add(new Presentation
            {
                Name = xmlPresentation.Name,
                Min = xmlPresentation.Min,
                Max = xmlPresentation.Max,
                PrintFormat = xmlPresentation.PrintFormat,
                Unit = xmlPresentation.Unit,
                Conversion = conversion
            });
        }

        return presentations;
    }

    #endregion

    #region Conversions

    public static List<XmlConversion> ToXmlConversions(IEnumerable<IValConversion> conversions, ILogger? logger = null)
    {
        var xmlConversions = new List<XmlConversion>();

        foreach (var conversion in conversions)
        {
            if (conversion is LinearValConversion linearConversion)
            {
                xmlConversions.Add(new XmlLinearConversion
                {
                    Name = linearConversion.Name,
                    Multiplier = linearConversion.Multiplier,
                    Offset = linearConversion.Offset
                });
            }
            else if (conversion is EnumValConversion enumConversion)
            {
                xmlConversions.Add(new XmlEnumConversion
                {
                    Name = enumConversion.Name,
                    Enums = (enumConversion.Enums ?? []).Select(e => new XmlEnum
                    {
                        Name = e.Name,
                        Value = e.Value
                    }).ToList()
                });
            }
            else
            {
                logger?.Log(LogLevel.Warn, $"Conversion type {conversion.GetType()} is not supported.");
            }
        }

        return xmlConversions;
    }

    public static List<IValConversion> FromXmlConversions(IEnumerable<XmlConversion> xmlConversions, ILogger? logger = null)
    {
        var conversions = new List<IValConversion>();

        foreach (var xmlConversion in xmlConversions)
        {
            if (!XmlModuleGlobal.TypeOfXmlConversion2ConversionEnumDict.TryGetValue(xmlConversion.GetType(), out var conversionType))
            {
                logger?.Log(LogLevel.Warn, $"Conversion type {xmlConversion.GetType()} is not supported.");
                continue;
            }

            try
            {
                var conversion = ConversionsGlobal.CreateInstance(conversionType);
                conversion.Name = xmlConversion.Name;

                if (conversion is LinearValConversion linearConversion && xmlConversion is XmlLinearConversion xmlLinearConversion)
                {
                    linearConversion.Multiplier = xmlLinearConversion.Multiplier;
                    linearConversion.Offset = xmlLinearConversion.Offset;
                }
                else if (conversion is EnumValConversion enumConversion && xmlConversion is XmlEnumConversion xmlEnumConversion)
                {
                    enumConversion.Enums = (xmlEnumConversion.Enums ?? [])
                        .Select(e => new EnumType { Name = e.Name, Value = e.Value })
                        .ToList();
                }

                conversions.Add(conversion);
            }
            catch (ArgumentException e)
            {
                logger?.Log(LogLevel.Error, $"Error creating conversion {xmlConversion.Name}: {e.Message}.");
            }
        }

        return conversions;
    }

    #endregion

    #region Variable events

    public static List<XmlVarEvent> ToXmlVarEvents(IEnumerable<IVarEvent> varEvents, ILogger? logger = null)
    {
        var xmlVarEvents = new List<XmlVarEvent>();

        foreach (var varEvent in varEvents)
        {
            if (varEvent is PeriodicVarEvent periodicVarEvent)
            {
                xmlVarEvents.Add(new PeriodicXmlVarEvent
                {
                    Name = periodicVarEvent.Name,
                    EventExtraParams = periodicVarEvent.EventExtraParams,
                    Period = periodicVarEvent.Period,
                    Unit = periodicVarEvent.Unit.ToString()
                });
            }
            else if (varEvent is OnRequestVarEvent)
            {
                xmlVarEvents.Add(new OnRequestXmlVarEvent { Name = varEvent.Name, EventExtraParams = varEvent.EventExtraParams });
            }
            else if (varEvent is OnValueChangedVarEvent onValueChangedVarEvent)
            {
                xmlVarEvents.Add(new OnValueChangedXmlVarEvent
                {
                    Name = onValueChangedVarEvent.Name,
                    EventExtraParams = onValueChangedVarEvent.EventExtraParams,
                    Threshold = onValueChangedVarEvent.Threshold
                });
            }
            else
            {
                logger?.Log(LogLevel.Warn, $"Variable event type {varEvent.GetType()} is not supported.");
            }
        }

        return xmlVarEvents;
    }

    public static List<IVarEvent> FromXmlVarEvents(IEnumerable<XmlVarEvent> xmlEvents, ILogger? logger = null)
    {
        var varEvents = new List<IVarEvent>();

        foreach (var xmlEvent in xmlEvents)
        {
            if (!XmlModuleGlobal.TypeOfXmlEvent2EventEnumDict.TryGetValue(xmlEvent.GetType(), out var varEventType))
            {
                logger?.Log(LogLevel.Warn, $"Variable event type {xmlEvent.GetType()} is not supported.");
                continue;
            }

            try
            {
                var varEvent = EventsGlobal.CreateInstance(varEventType);
                varEvent.Name = xmlEvent.Name;
                varEvent.EventExtraParams = xmlEvent.EventExtraParams;

                if (varEvent is PeriodicVarEvent periodicVarEvent && xmlEvent is PeriodicXmlVarEvent xmlPeriodicEvent)
                {
                    periodicVarEvent.Period = xmlPeriodicEvent.Period;
                    periodicVarEvent.Unit = Enum.Parse<TimeUnit>(xmlPeriodicEvent.Unit);
                }
                else if (varEvent is OnValueChangedVarEvent onValueChangedVarEvent && xmlEvent is OnValueChangedXmlVarEvent xmlOnValueChangedEvent)
                {
                    onValueChangedVarEvent.Threshold = xmlOnValueChangedEvent.Threshold;
                }

                varEvents.Add(varEvent);
            }
            catch (ArgumentException e)
            {
                logger?.Log(LogLevel.Error, $"Error creating variable event {xmlEvent.Name}: {e.Message}.");
            }
        }

        return varEvents;
    }

    #endregion

    #region Scripts

    public static List<XmlScript> ToXmlScripts(IEnumerable<IScriptBase> scripts, ILogger? logger = null)
    {
        var xmlScripts = new List<XmlScript>();

        foreach (var script in scripts)
        {
            if (!Enum.TryParse<XmlScriptExecutionMode>(script.ExecutionMode.ToString(), out var executionMode))
            {
                logger?.Log(LogLevel.Warn, $"Script execution mode {script.ExecutionMode} is not supported by XML module.");
                executionMode = XmlScriptExecutionMode.Manual;
            }

            xmlScripts.Add(new XmlScript
            {
                FileName = script.FileName,
                Content = script.Content,
                ExecutionMode = executionMode,
                Blocking = script.Blocking,
                TimeoutMs = script.TimeoutMs,
                AdditionalInfo = RemoveScriptExecutionOptions(script.AdditionalInfo),
                IsEnabled = script.IsEnabled,
                IsReplayEnabled = script.IsReplayEnabled
            });
        }

        return xmlScripts;
    }

    public static List<IScriptBase> FromXmlScripts(IEnumerable<XmlScript> xmlScripts)
    {
        var scripts = new List<IScriptBase>();

        foreach (var xmlScript in xmlScripts)
        {
            var additionalInfo = ExtractScriptExecutionOptions(
                xmlScript.AdditionalInfo,
                out var legacyBlocking,
                out var legacyTimeoutMs);

            scripts.Add(new PyScript
            {
                FileName = xmlScript.FileName,
                Content = xmlScript.Content,
                ExecutionMode = Enum.Parse<ScriptExecutionMode>(xmlScript.ExecutionMode.ToString()),
                Blocking = legacyBlocking ?? xmlScript.Blocking,
                TimeoutMs = legacyTimeoutMs ?? xmlScript.TimeoutMs,
                AdditionalInfo = additionalInfo,
                IsEnabled = xmlScript.IsEnabled,
                IsReplayEnabled = xmlScript.IsReplayEnabled
            });
        }

        return scripts;
    }

    private static string RemoveScriptExecutionOptions(string additionalInfo)
    {
        return ExtractScriptExecutionOptions(additionalInfo, out _, out _);
    }

    // Legacy: blocking/timeout used to be stored inside additionalInfo; they moved to
    // dedicated attributes, so they are stripped here and honored when reading old files.
    private static string ExtractScriptExecutionOptions(string additionalInfo, out bool? blocking, out double? timeoutMs)
    {
        blocking = null;
        timeoutMs = null;

        if (string.IsNullOrWhiteSpace(additionalInfo))
        {
            return string.Empty;
        }

        var parameters = new List<string>();
        var parts = additionalInfo.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        foreach (var part in parts)
        {
            var keyValue = part.Split('=', 2, StringSplitOptions.TrimEntries);
            if (keyValue.Length != 2)
            {
                parameters.Add(part);
                continue;
            }

            if (keyValue[0].Equals("blocking", StringComparison.OrdinalIgnoreCase))
            {
                if (TryParseBoolean(keyValue[1], out var parsedBlocking))
                {
                    blocking = parsedBlocking;
                }

                continue;
            }

            if (keyValue[0].Equals("timeout", StringComparison.OrdinalIgnoreCase))
            {
                if (double.TryParse(keyValue[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var parsedTimeoutMs) &&
                    parsedTimeoutMs >= 0)
                {
                    timeoutMs = parsedTimeoutMs;
                }

                continue;
            }

            parameters.Add(part);
        }

        return string.Join(";", parameters);
    }

    private static bool TryParseBoolean(string value, out bool result)
    {
        switch (value.Trim().ToLowerInvariant())
        {
            case "true":
            case "1":
            case "yes":
            case "y":
                result = true;
                return true;
            case "false":
            case "0":
            case "no":
            case "n":
                result = false;
                return true;
            default:
                result = false;
                return false;
        }
    }

    #endregion
}
