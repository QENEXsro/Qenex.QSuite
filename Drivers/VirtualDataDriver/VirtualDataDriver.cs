using System.Reflection;
using Qenex.QSuite.Common.CoreComm;
using Qenex.QSuite.Drivers.Driver;
using Qenex.QSuite.Protocols.Protocol;
using Qenex.QSuite.Specifications.Specification;

namespace Qenex.QSuite.Drivers.VirtualDataDriver;

/// <summary>
/// Virtual connection for script-computed variables: no transport and no timing of its own.
/// The hosted VirtualDataProtocol publishes values written by scripts; this driver only
/// starts and stops the protocols and, like the CAN, serial, TCP and simulation drivers,
/// carries the operator writes from controls to the owning protocol (which publishes them back).
/// </summary>
public class VirtualDataDriver : DriverBase, IProtocolVariableCommandDriver, ITransportSource<VirtualWrite>
{
    #region Constructors

    public VirtualDataDriver()
    {
        Specification = new SpecificationBase
        {
            Name = "VirtualDataDriver",
            Label = "Virtual Variables Host",
            Description = "Hosts variables computed by Python scripts; no device communication.",
            CreatedOn = new DateTime(2026, 7, 28),
            Version = Assembly.GetExecutingAssembly().GetName().Version ?? new Version(1, 0, 0),
            Author = "Qenex",
            Company = "QENEX Ltd."
        };
    }

    #endregion

    #region Configuration

    // The driver has no settings; any key in the project is reported as unknown (shared parser).
    public override void SetConfiguration()
    {
        SettingsParser.Parse(RawSettings, [], Logger, "Virtual Variables Host driver");
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

    // Same pattern as the other drivers: the module wires the value-changed and read-requested
    // notifications to this driver, which delegates to the owning protocol.
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

    // There is no device behind the driver, so there is nothing to send.
    public override void Send<T>(T data)
    {
    }

    public override Task SendAsync<T>(T data, CancellationToken ct = default)
    {
        return Task.CompletedTask;
    }

    #endregion
}
