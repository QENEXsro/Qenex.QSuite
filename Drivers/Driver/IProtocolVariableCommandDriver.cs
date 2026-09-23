using Qenex.QSuite.Protocols.Protocol;

namespace Qenex.QSuite.Drivers.Driver;

/// <summary>
/// Driver that carries operator commands for protocol variables to its protocols: an operator
/// write (value-changed notification → IProtocolVariableWriteProtocol) and an on-request read
/// (read-requested notification → IProtocolVariableReadProtocol). The driver only delegates to
/// the owning protocol, which executes the transaction over the driver's transport.
/// </summary>
public interface IProtocolVariableCommandDriver
{
    bool CanSendCommand(IProtocolVariable protocolVariable);
    Task OnProtocolVariableCommandAsync(IProtocolVariable protocolVariable, CancellationToken ct = default);

    /// <summary>True when one of this driver's protocols can read the variable on request.</summary>
    bool CanRequestRead(IProtocolVariable protocolVariable);

    /// <summary>Delegates a single on-request read to the owning protocol; throws when the read fails.</summary>
    Task OnProtocolVariableReadRequestAsync(IProtocolVariable protocolVariable, CancellationToken ct = default);
}
