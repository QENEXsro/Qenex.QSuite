using System.Collections.ObjectModel;
using System.Globalization;
using Qenex.QLibs.QUI;
using Qenex.QSuite.Variables.VariableEvents;

namespace Qenex.QInsight.ViewModels.ModelWrappers;

public class ProjectConfigurationEventWrapper : PropertyChangedBase
{
    private readonly List<EditablePropertyWrapper> editableProperties = [];
    private readonly IReadOnlyList<string> eventTypeOptions = EventsGlobal.VariableEventTypeDict.Keys
        .Where(type => type != EventsGlobal.VariableEventType.Undefined)
        .Select(type => type.ToString())
        .ToList();

    private EventState originalState;
    private EventState currentState;

    public ProjectConfigurationEventWrapper(IVarEvent variableEvent, bool isNew = false)
    {
        VariableEvent = variableEvent;
        IsNew = isNew;
        originalState = EventState.FromEvent(variableEvent);
        currentState = originalState;
        Properties = [];
        RebuildProperties();
    }

    public IVarEvent VariableEvent
    {
        get;
        private set
        {
            field = value;
            OnPropertyChanged();
        }
    }

    public bool IsNew { get; }
    public string Name => currentState.Name;
    public string OriginalName => originalState.Name;
    public string DisplayName => $"{currentState.Name} ({GetEventTypeName(currentState.VariableEventType)})";
    public ObservableCollection<EditablePropertyWrapper> Properties { get; }
    public bool HasChanges => !currentState.Equals(originalState);

    /// <summary>
    /// A changed type cannot be applied in place (the type is the event class), so a new
    /// instance is created and returned. The caller must then replace the previous instance in
    /// the project and rebuild every protocol variable that references it.
    /// </summary>
    public IVarEvent ApplyChanges()
    {
        if (currentState.VariableEventType != EventState.FromEvent(VariableEvent).VariableEventType)
        {
            VariableEvent = CreateEvent(currentState.VariableEventType);
        }

        VariableEvent.Name = currentState.Name;
        VariableEvent.EventExtraParams = currentState.ExtraParams;

        if (VariableEvent is PeriodicVarEvent periodicVarEvent && currentState.PeriodicState != null)
        {
            periodicVarEvent.Period = currentState.PeriodicState.Period;
            periodicVarEvent.Unit = currentState.PeriodicState.Unit;
        }
        else if (VariableEvent is OnValueChangedVarEvent onValueChangedVarEvent && currentState.OnValueChangedState != null)
        {
            onValueChangedVarEvent.Threshold = currentState.OnValueChangedState.Threshold;
        }

        originalState = currentState;
        NotifyStateChanged();
        return VariableEvent;
    }

    public void CancelChanges()
    {
        currentState = originalState;
        RebuildProperties();
        NotifyStateChanged();
    }

    /// <summary>
    /// Builds a standalone event from the wrapper's CURRENT (possibly not yet applied)
    /// state, so pending edits are included. Used for copying and for XML export.
    /// </summary>
    public IVarEvent CreateEventSnapshot(string name)
    {
        var snapshot = CreateEvent(name, currentState.VariableEventType);
        snapshot.EventExtraParams = currentState.ExtraParams;
        if (snapshot is PeriodicVarEvent periodicVarEvent && currentState.PeriodicState != null)
        {
            periodicVarEvent.Period = currentState.PeriodicState.Period;
            periodicVarEvent.Unit = currentState.PeriodicState.Unit;
        }
        else if (snapshot is OnValueChangedVarEvent onValueChangedVarEvent && currentState.OnValueChangedState != null)
        {
            onValueChangedVarEvent.Threshold = currentState.OnValueChangedState.Threshold;
        }

        return snapshot;
    }

    private void RebuildProperties()
    {
        editableProperties.Clear();
        Properties.Clear();

        AddProperty(Create("Event", "Name", () => currentState.Name, value => UpdateState(currentState with { Name = value })));
        AddProperty(Create("Event", "Extra Params", () => currentState.ExtraParams, value => UpdateState(currentState with { ExtraParams = value })));
        AddProperty(Create(
            "Event",
            "Type",
            () => currentState.VariableEventType,
            value => UpdateEventType(Parse<EventsGlobal.VariableEventType>(value)),
            eventTypeOptions));

        if (currentState.PeriodicState != null)
        {
            AddProperty(Create(
                "Periodic",
                "Period",
                () => currentState.PeriodicState.Period,
                value => UpdatePeriodicState(state => state with { Period = Parse<int>(value) })));
            AddProperty(Create(
                "Periodic",
                "Unit",
                () => currentState.PeriodicState.Unit,
                value => UpdatePeriodicState(state => state with { Unit = Parse<TimeUnit>(value) }),
                Enum.GetNames<TimeUnit>()));
        }
        else if (currentState.OnValueChangedState != null)
        {
            AddProperty(Create(
                "On Change",
                "Threshold",
                () => currentState.OnValueChangedState.Threshold,
                value => UpdateOnValueChangedState(state => state with { Threshold = Parse<double>(value) })));
        }

        RefreshProperties();
    }

    private void AddProperty(EditablePropertyWrapper property)
    {
        Properties.Add(property);
        editableProperties.Add(property);
    }

    private EditablePropertyWrapper Create(
        string group,
        string name,
        Func<object?> getValue,
        Action<string> setValue,
        IEnumerable<string>? options = null,
        bool isReadOnly = false)
    {
        return new EditablePropertyWrapper(group, name, getValue, setValue, null, options, isReadOnly);
    }

    private void UpdatePeriodicState(Func<PeriodicEventState, PeriodicEventState> update)
    {
        if (currentState.PeriodicState == null)
        {
            return;
        }

        UpdateState(currentState with { PeriodicState = update(currentState.PeriodicState) });
    }

    private void UpdateOnValueChangedState(Func<OnValueChangedEventState, OnValueChangedEventState> update)
    {
        if (currentState.OnValueChangedState == null)
        {
            return;
        }

        UpdateState(currentState with { OnValueChangedState = update(currentState.OnValueChangedState) });
    }

    private void UpdateEventType(EventsGlobal.VariableEventType variableEventType)
    {
        if (currentState.VariableEventType == variableEventType || variableEventType == EventsGlobal.VariableEventType.Undefined)
        {
            return;
        }

        currentState = CreateDefaultState(currentState.Name, currentState.ExtraParams, variableEventType);
        RebuildProperties();
        NotifyStateChanged();
    }

    private void UpdateState(EventState state)
    {
        currentState = state;
        NotifyStateChanged();
    }

    private void NotifyStateChanged()
    {
        OnPropertyChanged(nameof(Name));
        OnPropertyChanged(nameof(DisplayName));
        OnPropertyChanged(nameof(HasChanges));
    }

    private void RefreshProperties()
    {
        foreach (var property in editableProperties)
        {
            property.RefreshValue();
        }
    }

    private static T Parse<T>(string value)
    {
        return (T)ConvertTo(value, typeof(T));
    }

    private static object ConvertTo(string value, Type targetType)
    {
        if (targetType == typeof(string))
        {
            return value;
        }

        if (targetType.IsEnum)
        {
            return Enum.Parse(targetType, value, ignoreCase: true);
        }

        return Convert.ChangeType(value, targetType, CultureInfo.InvariantCulture);
    }

    private static IVarEvent CreateEvent(EventsGlobal.VariableEventType variableEventType)
    {
        var variableEvent = EventsGlobal.CreateInstance(variableEventType);
        if (variableEvent is PeriodicVarEvent periodicVarEvent)
        {
            periodicVarEvent.Period = 1;
            periodicVarEvent.Unit = TimeUnit.Sec;
        }
        else if (variableEvent is OnValueChangedVarEvent onValueChangedVarEvent)
        {
            onValueChangedVarEvent.Threshold = 0;
        }

        return variableEvent;
    }

    public static IVarEvent CreateEvent(string name, EventsGlobal.VariableEventType variableEventType)
    {
        var variableEvent = CreateEvent(variableEventType);
        variableEvent.Name = name;
        return variableEvent;
    }

    private static EventState CreateDefaultState(string name, string extraParams, EventsGlobal.VariableEventType variableEventType)
    {
        return variableEventType switch
        {
            EventsGlobal.VariableEventType.Periodic => new EventState(
                name,
                extraParams,
                EventsGlobal.VariableEventType.Periodic,
                new PeriodicEventState(1, TimeUnit.Sec),
                null),
            EventsGlobal.VariableEventType.OnValueChanged => new EventState(
                name,
                extraParams,
                EventsGlobal.VariableEventType.OnValueChanged,
                null,
                new OnValueChangedEventState(0)),
            EventsGlobal.VariableEventType.OnRequest => new EventState(
                name,
                extraParams,
                EventsGlobal.VariableEventType.OnRequest,
                null,
                null),
            _ => new EventState(name, extraParams, variableEventType, null, null)
        };
    }

    private static string GetEventTypeName(EventsGlobal.VariableEventType variableEventType)
    {
        return variableEventType.ToString().ToLowerInvariant();
    }

    private sealed record EventState(
        string Name,
        string ExtraParams,
        EventsGlobal.VariableEventType VariableEventType,
        PeriodicEventState? PeriodicState,
        OnValueChangedEventState? OnValueChangedState)
    {
        public static EventState FromEvent(IVarEvent variableEvent)
        {
            return variableEvent switch
            {
                PeriodicVarEvent periodicVarEvent => new EventState(
                    periodicVarEvent.Name,
                    periodicVarEvent.EventExtraParams,
                    EventsGlobal.VariableEventType.Periodic,
                    new PeriodicEventState(periodicVarEvent.Period, periodicVarEvent.Unit),
                    null),
                OnRequestVarEvent => new EventState(
                    variableEvent.Name,
                    variableEvent.EventExtraParams,
                    EventsGlobal.VariableEventType.OnRequest,
                    null,
                    null),
                OnValueChangedVarEvent onValueChangedVarEvent => new EventState(
                    onValueChangedVarEvent.Name,
                    onValueChangedVarEvent.EventExtraParams,
                    EventsGlobal.VariableEventType.OnValueChanged,
                    null,
                    new OnValueChangedEventState(onValueChangedVarEvent.Threshold)),
                _ => new EventState(variableEvent.Name, variableEvent.EventExtraParams, EventsGlobal.VariableEventType.Undefined, null, null)
            };
        }
    }

    private sealed record PeriodicEventState(int Period, TimeUnit Unit);

    private sealed record OnValueChangedEventState(double Threshold);
}
