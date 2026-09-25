using System.Reflection;
using Qenex.QSuite.Common.CoreComm;
using Qenex.QSuite.Drivers.Driver;
using Qenex.QSuite.Protocols.Protocol;
using Qenex.QSuite.Specifications.Specification;

namespace Qenex.QSuite.Drivers.SimDataDriver;

/// <summary>
/// Virtual connection for the simulation protocol: no transport and no timing of its own.
/// The hosted SimulDataProtocol generates the data itself in the periods of its variables'
/// events; this driver starts and stops the protocols and, like the CAN, serial and TCP
/// drivers, carries the operator commands (writes, on-request reads) to the owning protocol.
/// </summary>
public class SimDriver : DriverBase, IProtocolVariableCommandDriver, ITransportSource<int>
{
    #region Constructors

    public SimDriver()
    {
        Specification = new SpecificationBase()
        {
            Name = "SimulDataDriver",
            Label = "Simulation",
            Description = "Runs the Simulation Signals protocol - try a project without hardware.",
            CreatedOn = new DateTime(2025, 2, 1),
            Version = Assembly.GetExecutingAssembly().GetName().Version ?? new Version(1, 0, 0),
            Author = "Qenex",
            Company = "QENEX Ltd."
        };
    }

    #endregion

    #region Configuration

    // The driver has no settings. Legacy projects may still carry "periodes=..." (signal periods
    // now come from the variables' events); like every other plugin, an unknown key is reported
    // as a warning through the shared parser instead of being ignored silently.
    public override void SetConfiguration()
    {
        SettingsParser.Parse(RawSettings, [], Logger, "Simulation driver");
    }

    #endregion

    #region Driver control

    public override async Task StartAsync(CancellationToken ct = default)
    {
        if (!IsEnabled)
        {
            SetState(CommunicationState.Disabled);
            return;
        }

        SetState(CommunicationState.Starting);
        foreach (var protocol in Protocols)
        {
            await protocol.StartAsync(ct);
        }

        SetState(CommunicationState.Running);
    }

    public override async Task StopAsync(CancellationToken ct = default)
    {
        SetState(CommunicationState.Stopping);
        foreach (var protocol in Protocols)
        {
            await protocol.StopAsync(ct);
        }

        SetState(CommunicationState.Stopped);
    }

    public override void Dispose()
    {
    }

    #endregion

    #region Protocol variable commands (operator writes, on-request reads)

    // Same pattern as the CAN, serial and TCP client drivers: the module wires the value-changed
    // and read-requested notifications to this driver, which delegates to the owning protocol.
    // Without this the simulation variables have no read handler and every Read fails.
    public bool CanSendCommand(IProtocolVariable protocolVariable)
    {
        return Protocols
            .OfType<IProtocolVariableWriteProtocol>()
            .Any(protocol => protocol.CanWriteVariable(protocolVariable));
    }

    public async Task OnProtocolVariableCommandAsync(IProtocolVariable protocolVariable, CancellationToken ct = default)
    {
        foreach (var protocol in Protocols.OfType<IProtocolVariableWriteProtocol>())
        {
            if (!protocol.CanWriteVariable(protocolVariable))
            {
                continue;
            }

            await protocol.WriteVariableAsync(protocolVariable, ct);
            return;
        }
    }

    public bool CanRequestRead(IProtocolVariable protocolVariable)
    {
        return Protocols
            .OfType<IProtocolVariableReadProtocol>()
            .Any(protocol => protocol.CanReadVariable(protocolVariable));
    }

    // Failures propagate so the requesting control sees them (the protocol has logged the reason).
    public async Task OnProtocolVariableReadRequestAsync(IProtocolVariable protocolVariable, CancellationToken ct = default)
    {
        foreach (var protocol in Protocols.OfType<IProtocolVariableReadProtocol>())
        {
            if (!protocol.CanReadVariable(protocolVariable))
            {
                continue;
            }

            await protocol.ReadVariableAsync(protocolVariable, ct);
            return;
        }

        throw new InvalidOperationException(
            $"No protocol of this driver can read variable '{protocolVariable.Variable?.Name}' on request.");
    }

    #endregion

    #region Communication

    public override void Send<T>(T data)
    {
        throw new NotImplementedException();
    }

    public override Task SendAsync<T>(T data, CancellationToken ct = default)
    {
        throw new NotImplementedException();
    }

    #endregion
}
