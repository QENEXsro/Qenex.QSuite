using System.Reflection;
using Qenex.QSuite.Common.CoreComm;
using Qenex.QSuite.Drivers.Driver;
using Qenex.QSuite.Protocols.Protocol;
using Qenex.QSuite.Specifications.Specification;

namespace Qenex.QSuite.Drivers.VirtualDataDriver;

/// <summary>
/// Virtual connection for script-computed variables: no transport and no timing of its own.
/// The hosted VirtualDataProtocol publishes values written by scripts; this driver only
/// starts and stops the protocols.
/// </summary>
public class VirtualDataDriver : DriverBase, ITransportSource<VirtualWrite>
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
