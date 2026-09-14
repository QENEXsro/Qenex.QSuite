using System.Reflection;
using Qenex.QSuite.Common.CoreComm;
using Qenex.QSuite.LogSystems.LogSystem;
using Qenex.QSuite.Protocols.Modbus;
using Qenex.QSuite.Protocols.Protocol;
using Qenex.QSuite.Specifications.Specification;
using Qenex.QSuite.Variables.QVariables;
using Qenex.QSuite.Variables.VariableEvents;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Win32;

namespace Qenex.QSuite.Protocols.ModbusMaster;

/// <summary>
/// Modbus master (client): reads device data by polling per variable (data table + address from
/// the module XML, interval from the variable's periodic event) and writes on operator request
/// (FC 05/06/16). Framing is selected by the session settings — RTU (CRC16) over a serial driver
/// or Modbus TCP (MBAP) over a TCP driver — while the engine and this class stay byte-stream
/// agnostic: raw chunks in via AddReceivedDataToQueueAsync, framed requests out via the
/// transmitter injected by the hosting driver.
/// </summary>
public class ModbusMasterProtocol : ProtocolBase<byte[]>, ITransportProtocol<byte[]>, IProtocolVariableWriteProtocol
{
    private const int SchedulerIdleMs = 50;
    // On a plain Disconnect the driver reports the transport down a moment before it stops this
    // protocol; a short grace keeps a user-initiated stop from being reported as an outage.
    private const int TransportLossGraceMs = 250;

    private ModbusMasterSettings settings = new() { IsTcp = false };
    private string? configurationError = "Modbus master settings were not configured.";

    private volatile ModbusMasterEngine? engine;
    private volatile Func<byte[], CancellationToken, Task>? transmitter;

    private volatile bool exitRequested;
    // Set by the hosting driver via OnTransportConnectionChanged; the poll loop pauses while the
    // link is down and resumes by itself when the driver reports it back.
    private volatile bool transportConnected = true;
    private CancellationTokenSource? runCts;
    private Task? runTask;

    public ModbusMasterProtocol()
    {
        Specification = new SpecificationBase
        {
            Name = "ModbusMasterProtocol",
            Label = "Modbus Master (RTU/TCP)",
            Description = "Polls coils/registers and writes on demand. RTU: Serial Port; TCP: TCP Client.",
            CreatedOn = new DateTime(2026, 7, 10),
            Version = Assembly.GetExecutingAssembly().GetName().Version ?? new Version(1, 0, 0),
            Author = "Qenex",
            Company = "QENEX Ltd."
        };
    }

    // mode is mandatory: "rtu" for serial lines (CRC16), "tcp" for Modbus TCP (MBAP header),
    // matching the driver the protocol is hosted on.
    public override string DefaultRawSettings => "mode=rtu;unitId=1;timeoutMs=1000;retries=2";

    // Every supported commParam listed explicitly so the user only edits values instead of
    // discovering keys; address is mandatory with no sensible default, 0 is a placeholder to
    // overwrite. dataType/size stay derived from the variable on purpose (an explicit value
    // would break when the variable type changes).
    public override string CreateDefaultCommParam(IVariableBase variable, IEnumerable<IVarEvent> variableEvents)
    {
        return string.Join(";",
            "registerType=\"holdingRegister\"",
            "address=\"0\"",
            "wordOrder=\"big\"",
            "direction=\"read\"",
            $"eventRef=\"{GetDefaultEventName(variableEvents)}\"");
    }

    public override void SetConfiguration()
    {
        try
        {
            settings = ModbusMasterSettings.Parse(RawSettings);
            configurationError = null;
        }
        catch (ArgumentException e)
        {
            configurationError = $"Invalid Modbus master settings: {e.Message}";

            // A freshly added protocol arrives with empty settings — stay quiet until the user
            // applies something. Connecting still fails properly (StartAsync goes Faulted).
            if (!string.IsNullOrWhiteSpace(RawSettings))
            {
                Logger?.Log(LogLevel.Warn, configurationError);
            }
        }
    }

    /// <summary>Injected by the hosting driver (serial or TCP) while its link is usable.</summary>
    public void SetTransmitter(Func<byte[], CancellationToken, Task>? byteTransmitter)
    {
        transmitter = byteTransmitter;
    }

    /// <summary>
    /// The hosting driver reports link loss/recovery here (serial port unplugged, TCP peer gone).
    /// The poll loop keeps running: it waits while the link is down and continues polling the
    /// moment the driver reopens the transport, without the user having to disconnect/connect.
    /// </summary>
    public override void OnTransportConnectionChanged(bool connected)
    {
        transportConnected = connected;
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
                ProtocolVariableSpecification =
                    ModbusVariableSpecification.Create(commParams, variableEvents, variable, requirePollEvent: true)
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

        if (State == CommunicationState.Running || runTask is { IsCompleted: false })
        {
            return Task.CompletedTask;
        }

        if (!MasterSealValid())
        {
            SetState(CommunicationState.Stopped);
            return Task.CompletedTask;
        }

        SetState(CommunicationState.Starting);
        exitRequested = false;
        transportConnected = true;

        engine = new ModbusMasterEngine(settings.CreateFramer(), Logger)
        {
            TimeoutMs = settings.TimeoutMs,
            MaxRetries = settings.Retries,
            Transmitter = (frame, token) =>
                (transmitter ?? throw new ModbusProtocolException("Transport is not available (no transmitter injected)."))(frame, token)
        };

        runCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        // Task.Run: the loop must not inherit the caller's (UI) SynchronizationContext —
        // a blocked dispatcher (window drag, busy UI) would stall the whole session.
        // Capture the token now: a racing StopAsync may null runCts before the loop starts.
        var runToken = runCts.Token;
        runTask = Task.Run(() => PollLoopAsync(runToken));
        return Task.CompletedTask;
    }

    public override async Task StopAsync(CancellationToken ct = default)
    {
        SetState(CommunicationState.Stopping);
        exitRequested = true;

        if (runCts != null)
        {
            await runCts.CancelAsync();
        }

        if (runTask != null)
        {
            try
            {
                await runTask.WaitAsync(TimeSpan.FromSeconds(5), ct);
            }
            catch (OperationCanceledException)
            {
            }
            catch (TimeoutException)
            {
                Logger?.Log(LogLevel.Warn, "Modbus master protocol did not stop before timeout.");
            }
        }

        runCts?.Dispose();
        runCts = null;
        runTask = null;
        engine = null;
        SetState(CommunicationState.Stopped);
    }

    public override void Dispose()
    {
        runCts?.Dispose();
    }

    #endregion

    #region Polling

    private sealed class PollEntry
    {
        public required ModbusProtocolVariable ProtocolVariable { get; init; }
        public required ModbusVariableSpecification Spec { get; init; }
        public required IVariableBase Variable { get; init; }
        public required long IntervalMs { get; init; }
        public long NextDueMs { get; set; }
    }

    private async Task PollLoopAsync(CancellationToken ct)
    {
        try
        {
            var entries = BuildPollEntries();
            SetState(CommunicationState.Running,
                $"Modbus master ({(settings.IsTcp ? "TCP" : "RTU")}, unit {settings.UnitId}): polling {entries.Count} variable(s).");

            if (entries.Count == 0)
            {
                Logger?.Log(LogLevel.Debug, "Modbus master: no pollable variables configured; serving writes only.");
                await Task.Delay(Timeout.Infinite, ct);
                return;
            }

            while (!ct.IsCancellationRequested && !exitRequested)
            {
                await WaitForTransportAsync(entries, ct);

                var now = Environment.TickCount64;
                var anyDue = false;

                foreach (var entry in entries)
                {
                    if (entry.NextDueMs > now)
                    {
                        continue;
                    }

                    anyDue = true;
                    try
                    {
                        await PollVariableAsync(entry, ct);
                        if (StateMessage != null)
                        {
                            SetState(CommunicationState.Running);
                        }
                    }
                    catch (OperationCanceledException)
                    {
                        throw;
                    }
                    catch (Exception e)
                    {
                        // Any single poll failure — slave exception, timeout, or a transport error
                        // such as the serial port closing under a write — must not end the loop:
                        // the next cycle waits for the transport if needed and retries.
                        if (transportConnected)
                        {
                            Logger?.Log(LogLevel.Warn, $"Modbus master: poll of '{entry.Variable.Name}' failed: {e.Message}");
                            SetState(CommunicationState.Running, $"Poll of '{entry.Variable.Name}' failed: {e.Message}");
                        }
                        else
                        {
                            Logger?.Log(LogLevel.Debug, $"Modbus master: poll of '{entry.Variable.Name}' aborted, transport is down: {e.Message}");
                        }
                    }

                    // Re-anchor to now: after a stall the missed cycles are skipped instead of bursting.
                    entry.NextDueMs = Environment.TickCount64 + entry.IntervalMs;
                }

                if (!anyDue)
                {
                    var nextDueIn = entries.Min(e => e.NextDueMs) - Environment.TickCount64;
                    var delay = (int)Math.Clamp(nextDueIn, 1, SchedulerIdleMs);
                    await Task.Delay(delay, ct);
                }
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested || exitRequested)
        {
            // Normal stop.
        }
        catch (Exception e)
        {
            Logger?.Log(LogLevel.Error, $"Modbus master poll loop failed: {e.Message}");
            SetState(CommunicationState.Faulted, e.Message);
        }
    }

    /// <summary>
    /// Blocks while the driver reports the transport down. On resume the poll deadlines are
    /// re-anchored so the first cycle polls every variable once instead of bursting the cycles
    /// missed during the outage.
    /// </summary>
    private async Task WaitForTransportAsync(List<PollEntry> entries, CancellationToken ct)
    {
        if (transportConnected)
        {
            return;
        }

        // A stop cancels ct during the grace, so nothing is reported for a plain Disconnect.
        await Task.Delay(TransportLossGraceMs, ct);
        if (transportConnected)
        {
            return;
        }

        Logger?.Log(LogLevel.Info, "Modbus master: transport lost — polling paused until the driver reconnects.");
        SetState(CommunicationState.Faulted, "Transport lost — waiting for the driver to reconnect.");
        while (!transportConnected)
        {
            await Task.Delay(SchedulerIdleMs, ct);
        }

        Logger?.Log(LogLevel.Info, "Modbus master: transport is back — polling resumes.");
        var now = Environment.TickCount64;
        foreach (var entry in entries)
        {
            entry.NextDueMs = now;
        }

        SetState(CommunicationState.Running,
            $"Modbus master ({(settings.IsTcp ? "TCP" : "RTU")}, unit {settings.UnitId}): transport reconnected, polling {entries.Count} variable(s).");
    }

    private List<PollEntry> BuildPollEntries()
    {
        var entries = new List<PollEntry>();
        var now = Environment.TickCount64;

        foreach (var protocolVariable in Variables)
        {
            if (protocolVariable is not ModbusProtocolVariable modbusVariable ||
                !modbusVariable.IsCommunicated ||
                modbusVariable.ProtocolVariableSpecification is not ModbusVariableSpecification spec ||
                spec.Direction == CommDirection.Write ||
                modbusVariable.Variable is not (ScalarVariable or MatrixVariable))
            {
                continue;
            }

            var variable = modbusVariable.Variable;
            if (spec.VariableEvent is not PeriodicVarEvent periodicEvent)
            {
                Logger?.Log(LogLevel.Warn,
                    $"Modbus master: variable '{variable.Name}' has no periodic event; not polled.");
                continue;
            }

            var intervalMs = (long)periodicEvent.Period * (int)periodicEvent.Unit;
            if (intervalMs <= 0)
            {
                Logger?.Log(LogLevel.Warn,
                    $"Modbus master: variable '{variable.Name}' has a non-positive poll interval; not polled.");
                continue;
            }

            entries.Add(new PollEntry
            {
                ProtocolVariable = modbusVariable,
                Spec = spec,
                Variable = variable,
                IntervalMs = intervalMs,
                NextDueMs = now
            });
        }

        return entries;
    }

    private async Task PollVariableAsync(PollEntry entry, CancellationToken ct)
    {
        var session = engine ?? throw new ModbusProtocolException("Modbus master engine is not running.");

        object value;
        if (entry.Variable is MatrixVariable matrixVariable)
        {
            // The whole raw buffer travels as one register block; element endianness is decoded
            // by the variable itself, so wordOrder does not apply here.
            var registers = await session.ReadRegistersAsync(settings.UnitId, entry.Spec.RegisterType,
                entry.Spec.Address, entry.Spec.RegisterCount, ct);
            value = ModbusRegisterCodec.DecodeBytes(registers, matrixVariable.Size);
        }
        else if (entry.Spec.IsBitTable)
        {
            var bits = await session.ReadBitsAsync(settings.UnitId, entry.Spec.RegisterType, entry.Spec.Address, 1, ct);
            value = ModbusRegisterCodec.FromBit(bits[0], entry.Spec.DataType);
        }
        else
        {
            var registers = await session.ReadRegistersAsync(settings.UnitId, entry.Spec.RegisterType,
                entry.Spec.Address, entry.Spec.RegisterCount, ct);
            value = ModbusRegisterCodec.DecodeValue(registers, entry.Spec.DataType, entry.Spec.WordOrder);
        }

        entry.Variable.SetValue(value);
        entry.Variable.Timestamp = DateTime.UtcNow;

        // The operator-write path listens on this notification; the flag keeps a polled update from
        // echoing back out (NotifyValueChangedAsync awaits its handlers, so the scope holds).
        entry.ProtocolVariable.IsUpdatingFromBus = true;
        try
        {
            await entry.ProtocolVariable.NotifyValueChangedAsync();
        }
        finally
        {
            entry.ProtocolVariable.IsUpdatingFromBus = false;
        }
    }

    #endregion

    #region Operator writes

    public bool CanWriteVariable(IProtocolVariable protocolVariable)
    {
        return Variables.Contains(protocolVariable) &&
               protocolVariable is ModbusProtocolVariable { Variable: ScalarVariable or MatrixVariable } &&
               protocolVariable.ProtocolVariableSpecification is ModbusVariableSpecification
               {
                   Direction: CommDirection.Write or CommDirection.ReadWrite
               };
    }

    public async Task WriteVariableAsync(IProtocolVariable protocolVariable, CancellationToken ct = default)
    {
        if (protocolVariable is not ModbusProtocolVariable modbusVariable ||
            modbusVariable.IsUpdatingFromBus || // echo of our own poll update
            modbusVariable.ProtocolVariableSpecification is not ModbusVariableSpecification spec)
        {
            return;
        }

        var session = engine;
        if (session == null || State != CommunicationState.Running)
        {
            Logger?.Log(LogLevel.Warn,
                $"Modbus master: write of '{modbusVariable.Variable.Name}' skipped — protocol not running.");
            return;
        }

        if (modbusVariable.Variable is MatrixVariable matrixVariable)
        {
            await WriteMatrixElementsAsync(matrixVariable, spec, session, ct);
            return;
        }

        if (modbusVariable.Variable is not ScalarVariable scalarVariable)
        {
            return;
        }

        try
        {
            if (spec.RegisterType == ModbusRegisterType.Coil)
            {
                await session.WriteSingleCoilAsync(settings.UnitId, spec.Address,
                    ModbusRegisterCodec.ToBit(scalarVariable.GetValue()), ct);
            }
            else
            {
                var registers = ModbusRegisterCodec.EncodeValue(scalarVariable.GetValue(), spec.DataType, spec.WordOrder);
                await session.WriteRegistersAsync(settings.UnitId, spec.Address, registers, ct);
            }

            Logger?.Log(LogLevel.Info, $"Modbus master: wrote '{scalarVariable.Name}' at address {spec.Address}.");
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception e)
        {
            Logger?.Log(LogLevel.Warn, $"Modbus master: write of '{scalarVariable.Name}' failed: {e.Message}");
            SetState(CommunicationState.Running, $"Write of '{scalarVariable.Name}' failed: {e.Message}");
        }
    }

    /// <summary>
    /// Drains the pending element writes of a matrix variable. Each request writes only the
    /// registers its bytes touch (FC 06/16). The edited bytes are re-applied over the current
    /// buffer first: a poll may have replaced it since the edit, and a register shared with a
    /// neighbouring element must carry that neighbour's current value.
    /// </summary>
    private async Task WriteMatrixElementsAsync(MatrixVariable matrixVariable, ModbusVariableSpecification spec,
        ModbusMasterEngine session, CancellationToken ct)
    {
        while (matrixVariable.TryDequeuePendingWrite(out var request))
        {
            if (request.ByteOffset < 0 || request.Bytes.Length == 0 ||
                request.ByteOffset + request.Bytes.Length > matrixVariable.Size)
            {
                Logger?.Log(LogLevel.Warn,
                    $"Modbus master: pending write of '{matrixVariable.Name}' at byte {request.ByteOffset} " +
                    "no longer fits the matrix layout; skipped.");
                continue;
            }

            try
            {
                var buffer = matrixVariable.RawData;
                request.Bytes.CopyTo(buffer.AsSpan(request.ByteOffset));

                var firstRegister = request.ByteOffset / 2;
                var lastRegister = (request.ByteOffset + request.Bytes.Length - 1) / 2;
                var windowStart = firstRegister * 2;
                var windowLength = Math.Min((lastRegister + 1) * 2, buffer.Length) - windowStart;

                var registers = ModbusRegisterCodec.EncodeBytes(buffer.AsSpan(windowStart, windowLength));
                await session.WriteRegistersAsync(settings.UnitId, (ushort)(spec.Address + firstRegister), registers, ct);

                Logger?.Log(LogLevel.Info,
                    $"Modbus master: wrote {registers.Length} register(s) of '{matrixVariable.Name}' " +
                    $"at address {spec.Address + firstRegister}.");
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception e)
            {
                Logger?.Log(LogLevel.Warn, $"Modbus master: write of '{matrixVariable.Name}' failed: {e.Message}");
                SetState(CommunicationState.Running, $"Write of '{matrixVariable.Name}' failed: {e.Message}");
            }
        }
    }

    #endregion

    #region Process received data

    public override Task AddReceivedDataToQueueAsync(IEnumerable<byte[]> data, CancellationToken ct = default)
    {
        var session = engine;
        if (session == null)
        {
            return Task.CompletedTask;
        }

        foreach (var chunk in data)
        {
            session.OnBytesReceived(chunk);
        }

        return Task.CompletedTask;
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
        throw new NotSupportedException("Modbus master uses a request/response engine; bulk Encode is not supported.");
    }

    protected override IEnumerable<IProtocolVariable> Decode(IEnumerable<byte[]> data)
    {
        throw new NotSupportedException("Modbus master uses a request/response engine; bulk Decode is not supported.");
    }

    #endregion

    // LICENSE-SEAL COPY v1 — sync z _LicenseGuard/LicenseSealTemplate.cs
    // Nezavisly licencni check (necte licenseService). true=licencovano/nejasne (fail-open),
    // false=jiste nelicencovano. Reakce (nenastartovat) je v StartAsym.
    [System.Reflection.Obfuscation(Feature = "code control flow obfuscation", Exclude = false)]
    private static bool MasterSealValid()
    {
        try
        {
            const string pem =
                "-----BEGIN PUBLIC KEY-----\n" +
                "MFkwEwYHKoZIzj0CAQYIKoZIzj0DAQcDQgAEOACOwsviai2bRt16xogoH6vXPVtV\n" +
                "ziUZNXothbQLrl6y3K3GZfh49fajb50QT1zJ9XGjhJOc6SRNACtipO8eiA==\n" +
                "-----END PUBLIC KEY-----";
            const string product = "QInsight";
            const string prefix = "QLIC1";

            static byte[] Dec(string v)
            {
                var s = v.Replace('-', '+').Replace('_', '/');
                return Convert.FromBase64String(s.PadRight(s.Length + (4 - s.Length % 4) % 4, '='));
            }

            static string? Field(JsonElement o, string n)
            {
                if (o.ValueKind != JsonValueKind.Object) return null;
                foreach (var p in o.EnumerateObject())
                    if (string.Equals(p.Name, n, StringComparison.OrdinalIgnoreCase))
                        return p.Value.ValueKind == JsonValueKind.String ? p.Value.GetString() : null;
                return null;
            }

            var baseDir = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            if (string.IsNullOrEmpty(baseDir)) return true;
            var path = Path.Combine(baseDir, "Qenex", "QInsight", "license.json");
            if (!File.Exists(path)) return false;

            string? token;
            using (var sd = JsonDocument.Parse(File.ReadAllText(path)))
                token = Field(sd.RootElement, "Token");
            if (string.IsNullOrWhiteSpace(token)) return false;

            var parts = token.Split('.');
            if (parts.Length != 3 || parts[0] != prefix) return true;

            var payload = Dec(parts[1]);
            var signature = Dec(parts[2]);

            using (var key = ECDsa.Create())
            {
                key.ImportFromPem(pem);
                if (!key.VerifyData(payload, signature, HashAlgorithmName.SHA256)) return false;
            }

            using (var pd = JsonDocument.Parse(payload))
            {
                var pr = Field(pd.RootElement, "Product");
                if (pr is not null && !string.Equals(pr, product, StringComparison.Ordinal)) return false;

                var fp = Field(pd.RootElement, "MachineFingerprint");
                if (fp is not null)
                {
                    string src;
                    try
                    {
                        using var rk = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Cryptography");
                        var g = rk?.GetValue("MachineGuid") as string;
                        src = string.IsNullOrWhiteSpace(g) ? Environment.MachineName : g;
                    }
                    catch { src = Environment.MachineName; }

                    var mine = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(src)));
                    if (!string.Equals(fp, mine, StringComparison.Ordinal)) return false;
                }
            }

            return true;
        }
        catch
        {
            return true; // fail-open
        }
    }
}
