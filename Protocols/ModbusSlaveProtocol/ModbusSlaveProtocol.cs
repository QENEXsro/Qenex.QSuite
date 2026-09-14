using System.Reflection;
using Qenex.QSuite.Common.CoreComm;
using Qenex.QSuite.LogSystems.LogSystem;
using Qenex.QSuite.Protocols.Modbus;
using Qenex.QSuite.Protocols.Protocol;
using Qenex.QSuite.Specifications.Specification;
using Qenex.QSuite.Variables.QVariables;
using Qenex.QSuite.Variables.VariableEvents;

namespace Qenex.QSuite.Protocols.ModbusSlave;

/// <summary>
/// Modbus slave (server): exposes protocol variables as the four Modbus data tables. Remote reads
/// serve the variables' current values; remote writes (FC 05/06/16) assign them and raise the
/// value-changed notification. Framing (RTU over a serial driver, Modbus TCP over a TCP server
/// driver) is selected by the session settings; responses go out through the transmitter injected
/// by the hosting driver.
/// </summary>
public class ModbusSlaveProtocol : ProtocolBase<byte[]>, ITransportProtocol<byte[]>
{
    private ModbusSlaveSettings settings = new();
    private string? configurationError = "Modbus slave settings were not configured.";

    private volatile ModbusSlaveEngine? engine;
    private volatile IModbusFramer? framer;
    private volatile Func<byte[], CancellationToken, Task>? transmitter;
    private readonly SemaphoreSlim receiveLock = new(1, 1);

    public ModbusSlaveProtocol()
    {
        Specification = new SpecificationBase
        {
            Name = "ModbusSlaveProtocol",
            Label = "Modbus Slave (RTU/TCP)",
            Description = "Serves variables as coils/registers to a remote master. RTU: Serial Port; TCP: TCP Server.",
            CreatedOn = new DateTime(2026, 7, 10),
            Version = Assembly.GetExecutingAssembly().GetName().Version ?? new Version(1, 0, 0),
            Author = "Qenex",
            Company = "QENEX Ltd."
        };
    }

    // mode is mandatory ("rtu"/"tcp"). respondToAnyUnit defaults to true for TCP and false for RTU;
    // it is listed explicitly here so the operator sees it.
    public override string DefaultRawSettings => "mode=rtu;unitId=1;respondToAnyUnit=false";

    // The slave serves data on request, so there is no poll event; address 0 is a placeholder.
    public override string CreateDefaultCommParam(IVariableBase variable, IEnumerable<IVarEvent> variableEvents)
    {
        return string.Join(";",
            "registerType=\"holdingRegister\"",
            "address=\"0\"",
            "wordOrder=\"big\"",
            "direction=\"read\"");
    }

    public override void SetConfiguration()
    {
        try
        {
            settings = ModbusSlaveSettings.Parse(RawSettings, Logger);
            configurationError = null;
        }
        catch (ArgumentException e)
        {
            configurationError = $"Invalid Modbus slave settings: {e.Message}";

            // A freshly added protocol arrives with empty settings — stay quiet until the user
            // applies something. Connecting still fails properly (StartAsync goes Faulted).
            if (!string.IsNullOrWhiteSpace(RawSettings))
            {
                Logger?.Log(LogLevel.Warn, configurationError);
            }
        }
    }

    /// <summary>Injected by the hosting driver (serial or TCP server) while its link is usable.</summary>
    public void SetTransmitter(Func<byte[], CancellationToken, Task>? byteTransmitter)
    {
        transmitter = byteTransmitter;
    }

    #region Protocol variables

    public override IProtocolVariable? CreateProtocolVariable(IVariableBase variable, string commParams, bool isCommunicated)
    {
        return Build(variable, null, commParams, isCommunicated);
    }

    public override IProtocolVariable? CreateProtocolVariable(IVariableBase variable, IVarEvent variableEvent, string id)
    {
        return Build(variable, [variableEvent], $"address=\"{id}\";eventRef=\"{variableEvent.Name}\"", true);
    }

    public override IProtocolVariable? CreateProtocolVariable(IVariableBase variable, IEnumerable<IVarEvent> variableEvents,
        string commParams, bool isCommunicated)
    {
        return Build(variable, variableEvents, commParams, isCommunicated);
    }

    private ModbusProtocolVariable? Build(IVariableBase variable, IEnumerable<IVarEvent>? variableEvents, string commParams,
        bool isCommunicated)
    {
        try
        {
            return new ModbusProtocolVariable
            {
                Variable = variable,
                IsCommunicated = isCommunicated,
                // A slave serves values on request — no poll event is required.
                ProtocolVariableSpecification =
                    ModbusVariableSpecification.Create(commParams, variableEvents, variable, requirePollEvent: false)
            };
        }
        catch (Exception e)
        {
            Logger?.Log(LogLevel.Warn, $"Protocol variable specification for variable {variable.Name} could not be created ({e.Message}).");
            return null;
        }
    }

    #endregion

    #region Protocol control

    public override Task StartAsync(CancellationToken ct = default)
    {
        if (!IsEnabled)
        {
            SetState(CommunicationState.Disabled);
            return Task.CompletedTask;
        }

        if (configurationError != null)
        {
            SetState(CommunicationState.Faulted, configurationError);
            return Task.CompletedTask;
        }

        try
        {
            var map = ModbusVariableRegisterMap.Build(Variables,
                message => Logger?.Log(LogLevel.Warn, $"Modbus slave: {message}"));
            if (map.IsEmpty)
            {
                Logger?.Log(LogLevel.Warn, "Modbus slave: no variables mapped; every request will be rejected.");
            }

            engine = new ModbusSlaveEngine(map, Logger)
            {
                UnitId = settings.UnitId,
                RespondToAnyUnit = settings.RespondToAnyUnit
            };
            framer = settings.CreateFramer();
        }
        catch (ArgumentException e)
        {
            SetState(CommunicationState.Faulted, e.Message);
            Logger?.Log(LogLevel.Error, $"Modbus slave: {e.Message}");
            return Task.CompletedTask;
        }

        SetState(CommunicationState.Running,
            $"Modbus slave ({(settings.IsTcp ? "TCP" : "RTU")}) serving unit {settings.UnitId}.");
        return Task.CompletedTask;
    }

    public override Task StopAsync(CancellationToken ct = default)
    {
        engine = null;
        framer = null;
        SetState(CommunicationState.Stopped);
        return Task.CompletedTask;
    }

    public override void Dispose()
    {
    }

    #endregion

    #region Process received data

    public override async Task AddReceivedDataToQueueAsync(IEnumerable<byte[]> data, CancellationToken ct = default)
    {
        var currentEngine = engine;
        var currentFramer = framer;
        if (currentEngine == null || currentFramer == null)
        {
            return;
        }

        // Requests are processed strictly in arrival order; the framer is not thread-safe.
        await receiveLock.WaitAsync(ct);
        try
        {
            foreach (var chunk in data)
            {
                currentFramer.Append(chunk);
            }

            while (currentFramer.TryDequeueFrame(out var request))
            {
                var response = await currentEngine.ProcessRequestAsync(request, ct);
                if (response == null)
                {
                    continue; // addressed to another unit
                }

                var sendResponse = transmitter;
                if (sendResponse == null)
                {
                    Logger?.Log(LogLevel.Warn, "Modbus slave: response dropped — transport is not available.");
                    continue;
                }

                await sendResponse(currentFramer.Encode(response), ct);
            }
        }
        finally
        {
            receiveLock.Release();
        }
    }

    protected override void ProcessReceivedData(IEnumerable<byte[]> data)
    {
        AddReceivedDataToQueueAsync(data).GetAwaiter().GetResult();
    }

    protected override Task ProcessReceivedDataAsync(IEnumerable<byte[]> data, CancellationToken ct = default)
    {
        return AddReceivedDataToQueueAsync(data, ct);
    }

    #endregion

    #region Encode & Decode

    protected override IEnumerable<byte[]> Encode(IEnumerable<IProtocolVariable> protocolVariables)
    {
        throw new NotSupportedException("Modbus slave answers requests; bulk Encode is not supported.");
    }

    protected override IEnumerable<IProtocolVariable> Decode(IEnumerable<byte[]> data)
    {
        throw new NotSupportedException("Modbus slave answers requests; bulk Decode is not supported.");
    }

    #endregion
}
