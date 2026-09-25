namespace Qenex.QSuite.Protocols.Protocol;

/// <summary>
/// A protocol that reads a protocol variable from the target device once, on request — the
/// counterpart of <see cref="IProtocolVariableWriteProtocol"/>. Only request/response protocols
/// where QInsight is the master (XCP, Modbus master, simulation) implement it; protocols that
/// merely receive a data stream (RawCan, JSON signal, replay, Modbus slave…) do not, so the
/// Read action stays disabled for their variables.
/// A variable is readable on request when it is bound to an <c>OnRequestVarEvent</c>: such
/// variables are excluded from periodic polling and DAQ and are read only through
/// <see cref="ReadVariableAsync"/>. A driver implementing IProtocolVariableCommandDriver
/// delegates the module's read requests here.
/// </summary>
public interface IProtocolVariableReadProtocol
{
    /// <summary>True when this protocol owns the variable and its configuration allows an on-request read.</summary>
    bool CanReadVariable(IProtocolVariable protocolVariable);

    /// <summary>
    /// Reads the variable from the target device once and applies the value to the variable the
    /// same way a poll does (value, timestamp, value-changed notification). Throws on failure
    /// (timeout, device error, not connected) so the caller can report it.
    /// </summary>
    Task ReadVariableAsync(IProtocolVariable protocolVariable, CancellationToken ct = default);
}
