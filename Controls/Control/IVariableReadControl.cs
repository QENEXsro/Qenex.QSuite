using Qenex.QSuite.Variables.QVariables;

namespace Qenex.QSuite.Controls.Control;

/// <summary>
/// Control able to ask the device for a single read of a bound variable (On Request event).
/// Mirror of <see cref="IVariableWriteControl"/>: both delegates are injected by the host
/// (QInsight), the control knows nothing about protocols or drivers. A variable is readable on
/// request only when its protocol says so (bound to an On Request event on a request/response
/// protocol); for periodically polled variables the Read action stays disabled.
/// </summary>
public interface IVariableReadControl
{
    /// <summary>
    /// Host injects: true when the variable can be read from the device on request
    /// (decided by the protocol, e.g. an XCP/Modbus variable bound to an On Request event).
    /// </summary>
    Func<IVariableBase, bool>? CanReadVariableProvider { get; set; }

    /// <summary>
    /// Host injects: reads the variable from the device once. Returns false when the read failed
    /// (timeout, device error, not running); the value itself arrives through the usual
    /// value-changed path (UpdateVariableValueAsync) like a polled value.
    /// </summary>
    Func<IVariableBase, Task<bool>>? ReadVariableAsync { get; set; }

    /// <summary>
    /// Re-evaluates the read capability of the currently bound variables. Host calls it after
    /// injecting the delegates, after every (re)bind and after a configuration change.
    /// </summary>
    void RefreshReadCapability();
}
