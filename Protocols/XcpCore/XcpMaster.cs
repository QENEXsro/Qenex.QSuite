using Qenex.QSuite.LogSystems.LogSystem;

namespace Qenex.QSuite.Protocols.XcpCore;

/// <summary>
/// Transport-agnostic XCP master session engine. Owns the strictly serialized request/response
/// cycle (standard communication model: one command, one response), timeout detection with SYNCH
/// recovery and whole-transaction retries, and the connect/disconnect session state. Transmits
/// through the <see cref="Transmitter"/> delegate injected by the hosting protocol and consumes
/// received packets via <see cref="OnPacketReceived"/> — it knows nothing about CAN or TCP framing.
/// </summary>
public sealed class XcpMaster(ILogger? logger = null)
{
    // Per-packet data limits derived from the slave's MAX_CTO (CONNECT response, spec minimum 8).
    // Classic CAN (MAX_CTO=8, AG=1): RES carries up to 7 data bytes, DOWNLOAD up to 6. Larger CTOs
    // (XCP on Ethernet) fit any supported value (max 8 bytes) into a single packet, so chained
    // multi-packet transfers only ever happen on CAN.
    private int EffectiveMaxCto => Math.Max((int)(ConnectInfo?.MaxCto ?? 8), 8);
    private int MaxReadBytesPerPacket => EffectiveMaxCto - 1;
    private int MaxWriteBytesPerPacket => EffectiveMaxCto - 2;

    private readonly SemaphoreSlim requestLock = new(1, 1);

    // Serializes every transmitter call: commands (under the request lock) and STIM DTOs (sent
    // by the stimulation loop outside it) must never interleave their bytes on the transport.
    private readonly SemaphoreSlim transmitLock = new(1, 1);

    private volatile TaskCompletionSource<byte[]>? pendingResponse;
    private int pendingEventGeneration;

    // Commands whose wait was cancelled (Stop, transport lost) after the packet had left: the slave
    // still answers them. XCP carries no command id, so matching relies on order — the transport
    // delivers that late RES/ERR before the response to any command sent afterwards (DISCONNECT).
    // Each such response is consumed silently here instead of being reported as unsolicited or,
    // worse, taken for the answer to the next command. Reset on CONNECT (fresh session).
    private int lateResponsesExpected;

    /// <summary>Sends one XCP packet to the slave; injected by the hosting protocol while its
    /// transport is usable, null otherwise.</summary>
    public Func<byte[], CancellationToken, Task>? Transmitter { get; set; }

    /// <summary>Receives every DAQ DTO packet (PID ≤ 0xFB) from the slave. Called on the receive
    /// path — implementations must only enqueue, never block.</summary>
    public Action<byte[]>? DaqDtoReceived { get; set; }

    /// <summary>Response timeout per command; each EV_CMD_PENDING restarts it.</summary>
    public int TimeoutMs { get; set; } = 1000;

    /// <summary>How many times a timed-out transaction is retried (after SYNCH recovery).</summary>
    public int MaxRetries { get; set; } = 2;

    /// <summary>Codec configured with the slave's byte order after a successful connect.</summary>
    public XcpCodec Codec { get; } = new();

    public bool IsConnected { get; private set; }
    public XcpConnectResponse? ConnectInfo { get; private set; }

    /// <summary>GET_STATUS response captured during connect (resource protection for DAQ checks).</summary>
    public XcpStatusResponse? LastStatus { get; private set; }

    /// <summary>False when the slave has no calibration resource or protects it with seed &amp; key.</summary>
    public bool WritesAllowed { get; private set; }

    #region Receive path

    /// <summary>
    /// Demultiplexes one packet received from the slave by its PID. An incoming packet is never
    /// assumed to answer the pending command — RES/ERR complete it, events and service requests are
    /// out-of-band, DAQ DTOs are ignored until DAQ is implemented.
    /// </summary>
    public void OnPacketReceived(byte[] packet)
    {
        if (packet.Length == 0)
        {
            return;
        }

        switch (XcpPacket.Classify(packet[0]))
        {
            case XcpPacketKind.Response:
            case XcpPacketKind.Error:
                if (Volatile.Read(ref lateResponsesExpected) > 0)
                {
                    Interlocked.Decrement(ref lateResponsesExpected);
                    logger?.Log(LogLevel.Debug, $"XCP: late {XcpPacket.Classify(packet[0])} to a cancelled command consumed (PID 0x{packet[0]:X2}).");
                }
                else if (pendingResponse?.TrySetResult(packet) != true)
                {
                    logger?.Log(LogLevel.Warn, $"XCP: unsolicited {XcpPacket.Classify(packet[0])} packet (PID 0x{packet[0]:X2}) ignored.");
                }
                break;

            case XcpPacketKind.Event:
                HandleEvent(packet);
                break;

            case XcpPacketKind.ServiceRequest:
                logger?.Log(LogLevel.Debug, $"XCP: service request from slave ({BitConverter.ToString(packet)}).");
                break;

            case XcpPacketKind.DaqDto:
                DaqDtoReceived?.Invoke(packet);
                break;
        }
    }

    private void HandleEvent(byte[] packet)
    {
        var eventCode = packet.Length > 1 ? packet[1] : (byte)0xFF;
        switch (eventCode)
        {
            case XcpEventCode.CmdPending:
                // The slave asks to restart timeout detection; the command must NOT be repeated.
                Interlocked.Increment(ref pendingEventGeneration);
                logger?.Log(LogLevel.Debug, "XCP: EV_CMD_PENDING received, restarting timeout.");
                break;

            case XcpEventCode.SessionTerminated:
                IsConnected = false;
                logger?.Log(LogLevel.Warn, "XCP: session terminated by the slave (EV_SESSION_TERMINATED).");
                break;

            case XcpEventCode.DaqOverload:
                // D6: overload is a warning, never a session fault — the slave dropped samples
                // but keeps measuring; values catch up with the next transmitted cycle.
                logger?.Log(LogLevel.Warn,
                    "XCP: EV_DAQ_OVERLOAD — the slave's DAQ buffers overflowed and samples were lost; the session continues.");
                break;

            case XcpEventCode.StimTimeout:
                // S6: an event fired before fresh STIM data arrived — the slave stimulated with
                // old/partial data per its own policy. A warning, never a session fault.
                logger?.Log(LogLevel.Warn, $"XCP: EV_STIM_TIMEOUT — {DescribeStimTimeout(packet)}; the session continues.");
                break;

            default:
                logger?.Log(LogLevel.Debug, $"XCP: event 0x{eventCode:X2} from slave.");
                break;
        }
    }

    /// <summary>EV_STIM_TIMEOUT detail: info type at byte 2 (0 = event channel, 1 = DAQ list),
    /// the affected number as WORD at byte 4 (slave byte order).</summary>
    private string DescribeStimTimeout(byte[] packet)
    {
        if (packet.Length < 6)
        {
            return "an event fired before the slave had fresh stimulation data";
        }

        var number = Codec.ReadUInt16(packet.AsSpan(4, 2));
        return packet[2] == 0
            ? $"event channel {number} fired before the slave had fresh stimulation data"
            : $"DAQ list {number} could not (fully) be stimulated";
    }

    #endregion

    #region Session

    /// <summary>
    /// CONNECT → adopt byte order and address granularity → GET_STATUS → evaluate seed &amp; key
    /// protection. Throws <see cref="XcpProtocolException"/> for AG&gt;1 slaves (not supported yet).
    /// </summary>
    public Task<XcpStatusResponse> ConnectAsync(CancellationToken ct = default)
    {
        return ExecuteTransactionAsync(async token =>
        {
            // A fresh session: nothing from the previous one can still be in flight.
            Interlocked.Exchange(ref lateResponsesExpected, 0);
            var connectPacket = await ExecuteCommandAsync(XcpCodec.BuildConnect(), "CONNECT", token);
            var connect = XcpCodec.ParseConnectResponse(connectPacket);
            Codec.IsBigEndian = connect.IsBigEndian;

            if (connect.AddressGranularity != 1)
            {
                throw new XcpUnsupportedSlaveException(
                    $"Slave uses ADDRESS_GRANULARITY {(connect.AddressGranularity == 0 ? "reserved" : connect.AddressGranularity.ToString())}; only BYTE (AG=1) is supported.");
            }

            ConnectInfo = connect;
            IsConnected = true;

            var statusPacket = await ExecuteCommandAsync(XcpCodec.BuildGetStatus(), "GET_STATUS", token);
            var status = Codec.ParseGetStatusResponse(statusPacket);
            WritesAllowed = connect.SupportsCalibration && !status.IsCalibrationProtected;
            LastStatus = status;

            return status;
        }, ct);
    }

    /// <summary>Best-effort DISCONNECT: one attempt, failures are only logged.</summary>
    public async Task DisconnectAsync(CancellationToken ct = default)
    {
        await requestLock.WaitAsync(ct);
        try
        {
            await ExecuteCommandAsync(XcpCodec.BuildDisconnect(), "DISCONNECT", ct);
        }
        catch (Exception e) when (e is XcpErrorException or XcpTimeoutException)
        {
            logger?.Log(LogLevel.Warn, $"XCP: DISCONNECT failed ({e.Message}).");
        }
        finally
        {
            IsConnected = false;
            ConnectInfo = null;
            LastStatus = null;
            WritesAllowed = false;
            requestLock.Release();
        }
    }

    #endregion

    #region Memory transfer

    /// <summary>
    /// Reads <paramref name="size"/> bytes (1..8) from the ECU. Values that fit MAX_CTO − 1 are a
    /// single SHORT_UPLOAD; on classic CAN (7-byte limit) 8-byte values are chained as
    /// SHORT_UPLOAD(7) + UPLOAD(1) using the auto-incremented MTA (each frame individually
    /// acknowledged, no block mode). A chained transfer is not atomic.
    /// </summary>
    public Task<byte[]> ReadMemoryAsync(byte addressExtension, uint address, int size, CancellationToken ct = default)
    {
        EnsureConnected();
        if (size is < 1 or > 8)
        {
            throw new ArgumentOutOfRangeException(nameof(size), size, "XCP reads transfer 1..8 bytes.");
        }

        return ExecuteTransactionAsync(async token =>
        {
            var result = new byte[size];

            var firstCount = Math.Min(size, MaxReadBytesPerPacket);
            var first = await ExecuteCommandAsync(
                Codec.BuildShortUpload((byte)firstCount, addressExtension, address), "SHORT_UPLOAD", token);
            CopyResponseData(first, "SHORT_UPLOAD", result.AsSpan(0, firstCount));

            if (size > firstCount)
            {
                // SHORT_UPLOAD leaves the MTA behind the uploaded block (spec 1.6.1.2.8), so the
                // remainder continues from there.
                var remaining = size - firstCount;
                var second = await ExecuteCommandAsync(XcpCodec.BuildUpload((byte)remaining), "UPLOAD", token);
                CopyResponseData(second, "UPLOAD", result.AsSpan(firstCount, remaining));
            }

            return result;
        }, ct);
    }

    /// <summary>
    /// Writes bytes to the ECU as SET_MTA + DOWNLOAD (never SHORT_DOWNLOAD). Values larger than
    /// MAX_CTO − 2 are chained as consecutive DOWNLOADs at the auto-incremented MTA (on classic
    /// CAN 8 bytes = 6+2), each individually acknowledged. On timeout the whole transaction
    /// retries, which re-issues SET_MTA — the MTA is never trusted across a recovery.
    /// </summary>
    public Task WriteMemoryAsync(byte addressExtension, uint address, byte[] data, CancellationToken ct = default)
    {
        EnsureConnected();
        if (!WritesAllowed)
        {
            throw new XcpProtocolException(
                "Calibration writes are not available: the slave's calibration resource is missing or requires seed & key.");
        }

        if (data.Length is < 1 or > 8)
        {
            throw new ArgumentOutOfRangeException(nameof(data), data.Length, "XCP writes transfer 1..8 bytes.");
        }

        return ExecuteTransactionAsync<object?>(async token =>
        {
            await ExecuteCommandAsync(Codec.BuildSetMta(addressExtension, address), "SET_MTA", token);

            for (var offset = 0; offset < data.Length; offset += MaxWriteBytesPerPacket)
            {
                var chunk = data.AsSpan(offset, Math.Min(MaxWriteBytesPerPacket, data.Length - offset));
                await ExecuteCommandAsync(XcpCodec.BuildDownload(chunk), "DOWNLOAD", token);
            }

            return null;
        }, ct);
    }

    private void EnsureConnected()
    {
        if (!IsConnected)
        {
            throw new XcpProtocolException("Not connected to the XCP slave.");
        }
    }

    private static void CopyResponseData(byte[] response, string command, Span<byte> destination)
    {
        // RES layout for AG=1: 0xFF followed by the data elements; padding may follow.
        if (response.Length < 1 + destination.Length)
        {
            throw new XcpProtocolException(
                $"{command} response carries {response.Length - 1} data bytes, expected {destination.Length}.");
        }

        response.AsSpan(1, destination.Length).CopyTo(destination);
    }

    #endregion

    #region DAQ

    public Task<XcpDaqProcessorInfo> GetDaqProcessorInfoAsync(CancellationToken ct = default)
    {
        EnsureConnected();
        return ExecuteTransactionAsync(async token =>
        {
            var packet = await ExecuteCommandAsync(XcpCodec.BuildGetDaqProcessorInfo(), "GET_DAQ_PROCESSOR_INFO", token);
            return Codec.ParseDaqProcessorInfoResponse(packet);
        }, ct);
    }

    public Task<XcpDaqResolutionInfo> GetDaqResolutionInfoAsync(CancellationToken ct = default)
    {
        EnsureConnected();
        return ExecuteTransactionAsync(async token =>
        {
            var packet = await ExecuteCommandAsync(XcpCodec.BuildGetDaqResolutionInfo(), "GET_DAQ_RESOLUTION_INFO", token);
            return Codec.ParseDaqResolutionInfoResponse(packet);
        }, ct);
    }

    /// <summary>
    /// Queries one ECU event channel. The command leaves the MTA pointing at the channel name,
    /// which is uploaded within the same transaction (chained UPLOADs for names longer than one
    /// packet), so the two steps can never interleave with other commands.
    /// </summary>
    public Task<(XcpDaqEventInfo Info, string Name)> GetDaqEventInfoAsync(ushort eventChannel, CancellationToken ct = default)
    {
        EnsureConnected();
        return ExecuteTransactionAsync(async token =>
        {
            var packet = await ExecuteCommandAsync(Codec.BuildGetDaqEventInfo(eventChannel), "GET_DAQ_EVENT_INFO", token);
            var info = XcpCodec.ParseDaqEventInfoResponse(packet);

            var name = string.Empty;
            if (info.NameLength > 0)
            {
                var nameBytes = new byte[info.NameLength];
                for (var offset = 0; offset < nameBytes.Length; offset += MaxReadBytesPerPacket)
                {
                    var count = Math.Min(MaxReadBytesPerPacket, nameBytes.Length - offset);
                    var part = await ExecuteCommandAsync(XcpCodec.BuildUpload((byte)count), "UPLOAD", token);
                    CopyResponseData(part, "UPLOAD", nameBytes.AsSpan(offset, count));
                }

                name = System.Text.Encoding.ASCII.GetString(nameBytes).TrimEnd('\0');
            }

            return (info, name);
        }, ct);
    }

    /// <summary>
    /// Replays the whole DAQ/STIM configuration to the slave and starts the transfer, as one
    /// transaction: FREE_DAQ → ALLOC_DAQ/ALLOC_ODT/ALLOC_ODT_ENTRY → WRITE_DAQ per entry →
    /// SET_DAQ_LIST_MODE → START_STOP_DAQ_LIST (select) → START_STOP_SYNCH (start selected).
    /// STIM lists (S4) differ only in their mode byte: the DIRECTION bit, with the timestamp
    /// bit governed by <paramref name="includeStimTimestamp"/>. The timestamp bit is the
    /// master's choice per list (QFW SDK slaves honour it for DAQ and STIM alike; the caller
    /// sets it for DAQ only with daqTimestamps=slave and for STIM only when a legacy
    /// TIMESTAMP_FIXED slave would reject switching it off with ERR_CMD_SYNTAX). A timeout
    /// retry restarts from FREE_DAQ, so the slave never keeps a half-built config. Returns
    /// FIRST_PID per list (meaningful for absolute-PID identification only).
    /// </summary>
    public Task<byte[]> ConfigureAndStartDaqAsync(IReadOnlyList<XcpDaqListPlan> lists, bool includeTimestamp,
        bool includeStimTimestamp = false, CancellationToken ct = default)
    {
        EnsureConnected();
        if (lists.Count == 0)
        {
            throw new ArgumentException("At least one DAQ list is required.", nameof(lists));
        }

        return ExecuteTransactionAsync(async token =>
        {
            await ExecuteCommandAsync(XcpCodec.BuildFreeDaq(), "FREE_DAQ", token);
            await ExecuteCommandAsync(Codec.BuildAllocDaq((ushort)lists.Count), "ALLOC_DAQ", token);

            for (var daq = 0; daq < lists.Count; daq++)
            {
                await ExecuteCommandAsync(Codec.BuildAllocOdt((ushort)daq, (byte)lists[daq].Odts.Count), "ALLOC_ODT", token);
            }

            for (var daq = 0; daq < lists.Count; daq++)
            {
                for (var odt = 0; odt < lists[daq].Odts.Count; odt++)
                {
                    await ExecuteCommandAsync(
                        Codec.BuildAllocOdtEntry((ushort)daq, (byte)odt, (byte)lists[daq].Odts[odt].Count),
                        "ALLOC_ODT_ENTRY", token);
                }
            }

            for (var daq = 0; daq < lists.Count; daq++)
            {
                for (var odt = 0; odt < lists[daq].Odts.Count; odt++)
                {
                    // WRITE_DAQ auto-increments the DAQ pointer, so it is set once per ODT.
                    await ExecuteCommandAsync(Codec.BuildSetDaqPtr((ushort)daq, (byte)odt, 0), "SET_DAQ_PTR", token);
                    foreach (var entry in lists[daq].Odts[odt])
                    {
                        await ExecuteCommandAsync(
                            Codec.BuildWriteDaq(entry.Size, entry.AddressExtension, entry.Address), "WRITE_DAQ", token);
                    }
                }
            }

            for (var daq = 0; daq < lists.Count; daq++)
            {
                var mode = lists[daq].IsStim
                    ? (byte)(XcpDaqListModeBits.Direction | (includeStimTimestamp ? XcpDaqListModeBits.Timestamp : 0))
                    : includeTimestamp ? XcpDaqListModeBits.Timestamp : (byte)0;
                await ExecuteCommandAsync(
                    Codec.BuildSetDaqListMode(mode, (ushort)daq, lists[daq].EventChannel, prescaler: 1, priority: 0),
                    "SET_DAQ_LIST_MODE", token);
            }

            var firstPids = new byte[lists.Count];
            for (var daq = 0; daq < lists.Count; daq++)
            {
                var response = await ExecuteCommandAsync(
                    Codec.BuildStartStopDaqList(XcpDaqStartStopMode.Select, (ushort)daq), "START_STOP_DAQ_LIST", token);
                firstPids[daq] = XcpCodec.ParseStartStopDaqListResponse(response);
            }

            await ExecuteCommandAsync(XcpCodec.BuildStartStopSynch(XcpDaqSynchMode.StartSelected), "START_STOP_SYNCH", token);
            return firstPids;
        }, ct);
    }

    /// <summary>
    /// Sends one STIM DTO packet to the slave. DTOs are fire-and-forget (no response, no PID in
    /// the CTO space), so they bypass the request/response engine entirely and only serialize
    /// against command transmissions via the transmit lock (S7). Transport failures surface as
    /// exceptions for the stimulation loop's failure counting.
    /// </summary>
    public async Task SendStimDtoAsync(byte[] packet, CancellationToken ct = default)
    {
        EnsureConnected();
        var currentTransmitter = Transmitter
                                 ?? throw new XcpProtocolException("XCP transport is not available (no transmitter injected).");

        await transmitLock.WaitAsync(ct);
        try
        {
            await currentTransmitter(packet, ct);
        }
        finally
        {
            transmitLock.Release();
        }
    }

    /// <summary>Best-effort stop of all DAQ lists; failures are only logged (the slave stops DAQ
    /// on DISCONNECT anyway).</summary>
    public async Task StopDaqAsync(CancellationToken ct = default)
    {
        try
        {
            await ExecuteTransactionAsync<object?>(async token =>
            {
                await ExecuteCommandAsync(XcpCodec.BuildStartStopSynch(XcpDaqSynchMode.StopAll), "START_STOP_SYNCH", token);
                return null;
            }, ct);
        }
        catch (Exception e) when (e is XcpErrorException or XcpTimeoutException or XcpProtocolException)
        {
            logger?.Log(LogLevel.Warn, $"XCP: stopping DAQ failed ({e.Message}).");
        }
    }

    #endregion

    #region Request/response engine

    /// <summary>
    /// Runs one transaction (a sequence of commands that must not be interleaved with others) under
    /// the request lock. On timeout the command processor is re-synchronized with SYNCH and the
    /// whole transaction retried up to <see cref="MaxRetries"/> times — MTA-dependent sequences
    /// thereby naturally re-issue their SET_MTA.
    /// </summary>
    private async Task<TResult> ExecuteTransactionAsync<TResult>(
        Func<CancellationToken, Task<TResult>> transaction, CancellationToken ct)
    {
        await requestLock.WaitAsync(ct);
        try
        {
            for (var attempt = 0; ; attempt++)
            {
                try
                {
                    return await transaction(ct);
                }
                catch (XcpTimeoutException e) when (attempt < MaxRetries)
                {
                    logger?.Log(LogLevel.Warn, $"XCP: {e.Message} Re-synchronizing (attempt {attempt + 1}/{MaxRetries}).");
                    await TrySynchAsync(ct);
                }
            }
        }
        finally
        {
            requestLock.Release();
        }
    }

    /// <summary>Sends SYNCH after a timeout. The expected answer is ERR_CMD_SYNCH; its absence is
    /// only logged — the following retry will fail fast if the slave is really gone.</summary>
    private async Task TrySynchAsync(CancellationToken ct)
    {
        try
        {
            await ExecuteCommandAsync(XcpCodec.BuildSynch(), "SYNCH", ct);
            logger?.Log(LogLevel.Warn, "XCP: SYNCH got a positive response; expected ERR_CMD_SYNCH.");
        }
        catch (XcpErrorException e) when (e.ErrorCode == XcpErrorCode.CmdSynch)
        {
            // ERR_CMD_SYNCH is the defined (successful) answer to SYNCH.
        }
        catch (XcpTimeoutException)
        {
            logger?.Log(LogLevel.Warn, "XCP: SYNCH itself timed out.");
        }
    }

    /// <summary>
    /// One command, one response: transmits the packet and waits for RES/ERR. EV_CMD_PENDING
    /// restarts the timeout window without re-sending. ERR becomes <see cref="XcpErrorException"/>;
    /// a genuine timeout becomes <see cref="XcpTimeoutException"/> (recovery is the transaction
    /// wrapper's job). Must only be called while holding the request lock.
    /// </summary>
    private async Task<byte[]> ExecuteCommandAsync(byte[] command, string commandName, CancellationToken ct)
    {
        var transmitter = Transmitter
                          ?? throw new XcpProtocolException("XCP transport is not available (no transmitter injected).");

        var responseSource = new TaskCompletionSource<byte[]>(TaskCreationOptions.RunContinuationsAsynchronously);
        pendingResponse = responseSource;
        var sent = false;
        try
        {
            await transmitLock.WaitAsync(ct);
            try
            {
                await transmitter(command, ct);
                sent = true;
            }
            finally
            {
                transmitLock.Release();
            }

            while (true)
            {
                var generationAtWaitStart = Volatile.Read(ref pendingEventGeneration);
                var completed = await Task.WhenAny(responseSource.Task, Task.Delay(TimeoutMs, ct));

                if (completed == responseSource.Task)
                {
                    var packet = await responseSource.Task;
                    if (packet[0] == 0xFE)
                    {
                        throw new XcpErrorException(packet.Length > 1 ? packet[1] : XcpErrorCode.Generic);
                    }

                    return packet;
                }

                ct.ThrowIfCancellationRequested();

                if (Volatile.Read(ref pendingEventGeneration) != generationAtWaitStart)
                {
                    continue; // EV_CMD_PENDING arrived — restart the timeout, do not repeat the command.
                }

                throw new XcpTimeoutException(commandName);
            }
        }
        finally
        {
            pendingResponse = null;
            // Cancelled after the packet left and before the slave answered: the answer is still
            // on its way (TrySetCanceled fails once a response has already been delivered). A
            // timeout is deliberately NOT counted — the slave may never answer and SYNCH recovery
            // must see the next packet.
            if (sent && ct.IsCancellationRequested && responseSource.TrySetCanceled())
            {
                Interlocked.Increment(ref lateResponsesExpected);
            }
        }
    }

    #endregion
}
