using System.Globalization;
using System.Diagnostics;
using System.Reflection;
using System.Text;
using MessagePack;
using Qenex.QSuite.Common.CoreComm;
using Qenex.QSuite.Drivers.Driver;
using Qenex.QSuite.LogSystems.LogSystem;
using Qenex.QSuite.Protocols.Protocol;
using Qenex.QSuite.Specifications.Specification;

namespace Qenex.QSuite.Drivers.FileDataReplayDriver;

public class FileDataReplayDriver : DriverBase, IReplayDriver, IDataLogCsvExportDriver, ITransportSource<DataLogRecord>
{
    private static readonly TimeSpan DataLoadProgressUpdateInterval = TimeSpan.FromMilliseconds(100);
    private string logFilePath = Path.Combine(DriverEnvironment.DataRootDirectory, "DataLogs", "values.qilog");
    private bool loop;
    private double speed = 1.0;
    private ReplayMode replayMode = ReplayMode.Realtime;
    private CancellationTokenSource? replayCancellation;
    private CancellationTokenSource? dataLoadCancellation;
    private Task? dataLoadTask;
    private Task? replayTask;
    private readonly object replayStateLock = new();
    private readonly SemaphoreSlim replayRecordGate = new(1, 1);
    private readonly SemaphoreSlim replayRecordsAvailable = new(0);
    private List<DataLogRecord> replayRecords = [];
    private int replayRecordIndex;
    private long replayFirstTimestampUtcTicks;
    private long replayCurrentTimestampUtcTicks;
    private int replaySeekVersion;
    private long lastDataLoadProgressTimestamp;
    private bool isPaused;
    private bool isDataLoaded;
    private bool isDataLoading;
    private volatile bool completeDataLoadOnCancellation;
    private long lastLoadedTimestampUtcTicks;
    private bool loadedRecordsOutOfOrder;

    public FileDataReplayDriver()
    {
        Specification = new SpecificationBase
        {
            Name = "FileDataReplayDriver",
            Label = "Data Log Replay",
            Description = "Plays a recorded .qilog file back into the project (added by Import).",
            CreatedOn = new DateTime(2026, 5, 25),
            Version = Assembly.GetExecutingAssembly().GetName().Version ?? new Version(1, 0, 0),
            Author = "Qenex",
            Company = "QENEX Ltd."
        };
    }

    public event EventHandler? ReplayCompleted;
    public event EventHandler<ReplayProgressChangedEventArgs>? ReplayProgressChanged;

    public TimeSpan CurrentTime { get; private set; }
    public TimeSpan Duration { get; private set; }

    public bool IsDataLoaded
    {
        get
        {
            lock (replayStateLock)
            {
                return isDataLoaded;
            }
        }
    }

    public bool IsDataLoading
    {
        get
        {
            lock (replayStateLock)
            {
                return isDataLoading;
            }
        }
    }

    public bool IsPaused
    {
        get
        {
            lock (replayStateLock)
            {
                return isPaused;
            }
        }
    }

    public override string DefaultRawSettings => "file=DataLogs\\values.qilog;mode=realtime;speed=1;loop=false";

    private static readonly string[] KnownSettings = ["file", "directory", "mode", "speed", "loop"];

    public override void SetConfiguration()
    {
        var settings = SettingsParser.Parse(RawSettings, KnownSettings, Logger, "Data log replay driver");
        if (settings.TryGetValue("file", out var configuredFile) && !string.IsNullOrWhiteSpace(configuredFile))
        {
            logFilePath = configuredFile;
        }

        if (settings.TryGetValue("directory", out var directory) && !string.IsNullOrWhiteSpace(directory))
        {
            var fileName = Path.GetFileName(logFilePath);
            logFilePath = Path.Combine(directory, fileName);
        }

        if (!Path.IsPathRooted(logFilePath))
        {
            logFilePath = Path.Combine(DriverEnvironment.DataRootDirectory, logFilePath);
        }

        if (settings.TryGetValue("loop", out var loopValue))
        {
            if (bool.TryParse(loopValue, out var parsedLoop))
            {
                loop = parsedLoop;
            }
            else
            {
                Logger?.Log(LogLevel.Warn, $"Replay: invalid loop value '{loopValue}', keeping {loop}.");
            }
        }

        if (settings.TryGetValue("speed", out var speedValue))
        {
            // Invariant culture on purpose: "speed=1.5" must work regardless of the OS locale.
            if (double.TryParse(speedValue, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsedSpeed)
                && parsedSpeed > 0)
            {
                speed = parsedSpeed;
            }
            else
            {
                Logger?.Log(LogLevel.Warn, $"Replay: invalid speed value '{speedValue}', keeping {speed.ToString(CultureInfo.InvariantCulture)}.");
            }
        }

        if (settings.TryGetValue("mode", out var modeValue))
        {
            if (Enum.TryParse<ReplayMode>(modeValue, ignoreCase: true, out var parsedMode))
            {
                replayMode = parsedMode;
            }
            else
            {
                Logger?.Log(LogLevel.Warn, $"Replay: invalid mode value '{modeValue}', keeping {replayMode}.");
            }
        }
    }

    public void StartLoadingData(CancellationToken ct = default)
    {
        lock (replayStateLock)
        {
            if (isDataLoaded || isDataLoading || dataLoadTask is { IsCompleted: false })
            {
                return;
            }

            isDataLoading = true;
        }

        dataLoadCancellation?.Dispose();
        completeDataLoadOnCancellation = false;
        dataLoadCancellation = CancellationTokenSource.CreateLinkedTokenSource(ct);
        dataLoadTask = LoadReplayRecordsSafeAsync(dataLoadCancellation.Token);
    }

    public void CancelLoadingData()
    {
        if (!IsDataLoading)
        {
            return;
        }

        completeDataLoadOnCancellation = true;
        dataLoadCancellation?.Cancel();
    }

    public override Task StartAsync(CancellationToken ct = default)
    {
        if (!IsEnabled)
        {
            SetState(CommunicationState.Disabled);
            return Task.CompletedTask;
        }

        SetState(CommunicationState.Starting);
        foreach (var protocol in Protocols)
        {
            _ = protocol.StartAsync(ct);
        }

        StartLoadingData(ct);
        replayCancellation = CancellationTokenSource.CreateLinkedTokenSource(ct);
        replayTask = RunReplayAsync(replayCancellation.Token);
        return Task.CompletedTask;
    }

    public override async Task StopAsync(CancellationToken ct = default)
    {
        SetState(CommunicationState.Stopping);
        Resume();
        replayCancellation?.Cancel();
        if (replayTask != null)
        {
            try
            {
                await replayTask;
            }
            catch (OperationCanceledException)
            {
            }
        }

        ResetReplayPosition();

        foreach (var protocol in Protocols)
        {
            await protocol.StopAsync(ct);
        }

        replayCancellation?.Dispose();
        replayCancellation = null;
        replayTask = null;
    }

    public override void Dispose()
    {
        replayCancellation?.Cancel();
        replayCancellation?.Dispose();
        dataLoadCancellation?.Cancel();
        completeDataLoadOnCancellation = false;
        dataLoadCancellation?.Dispose();
        replayRecordGate.Dispose();
        replayRecordsAvailable.Dispose();
    }

    public override void Send<T>(T data)
    {
        throw new NotSupportedException("File data replay driver does not send command data.");
    }

    public override Task SendAsync<T>(T data, CancellationToken ct = default)
    {
        throw new NotSupportedException("File data replay driver does not send command data.");
    }

    public void Pause()
    {
        lock (replayStateLock)
        {
            if (State != CommunicationState.Running || isPaused)
            {
                return;
            }

            isPaused = true;
        }

        RaiseReplayProgressChanged();
    }

    public void Resume()
    {
        lock (replayStateLock)
        {
            if (!isPaused)
            {
                return;
            }

            isPaused = false;
        }

        RaiseReplayProgressChanged();
    }

    public async Task SeekAsync(TimeSpan position, CancellationToken ct = default)
    {
        if (!IsDataLoaded)
        {
            Logger?.Log(LogLevel.Warn, "Replay seek is available after the data log is fully loaded.");
            return;
        }

        if (position < TimeSpan.Zero)
        {
            position = TimeSpan.Zero;
        }

        if (position > Duration)
        {
            position = Duration;
        }

        List<DataLogRecord> recordsToReplay;
        lock (replayStateLock)
        {
            var targetTimestampUtcTicks = replayFirstTimestampUtcTicks + position.Ticks;
            replayRecordIndex = FindFirstRecordIndexAfter(targetTimestampUtcTicks);
            replayCurrentTimestampUtcTicks = targetTimestampUtcTicks;
            CurrentTime = position;
            replaySeekVersion++;
            recordsToReplay = GetRecordsUpTo(targetTimestampUtcTicks);
        }

        await replayRecordGate.WaitAsync(ct);
        try
        {
            await PublishRecordsAsync(recordsToReplay, ct);
        }
        finally
        {
            replayRecordGate.Release();
        }

        RaiseReplayProgressChanged();
    }

    public async Task ExportCsvAsync(string filePath, CancellationToken ct = default)
    {
        StartLoadingData(ct);
        var currentDataLoadTask = dataLoadTask;
        if (currentDataLoadTask != null)
        {
            await currentDataLoadTask;
        }

        List<DataLogRecord> records;
        lock (replayStateLock)
        {
            records = replayRecords
                .OrderBy(GetRecordTimestampUtcTicks)
                .ToList();
        }

        var directory = Path.GetDirectoryName(filePath);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        await using var stream = new FileStream(
            filePath,
            FileMode.Create,
            FileAccess.Write,
            FileShare.Read,
            bufferSize: 4096,
            useAsync: true);
        await using var writer = new StreamWriter(stream, new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));

        await writer.WriteLineAsync(CreateCsvRow(["Source file", Path.GetFileName(logFilePath)]));
        await writer.WriteLineAsync(CreateCsvRow(["Exported at", DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture)]));
        await writer.WriteLineAsync();

        var columns = CreateCsvColumns(records);
        var headerRow = new List<string> { "TimestampUtc" };
        headerRow.AddRange(columns.Select(column => column.Header));
        await writer.WriteLineAsync(CreateCsvRow(headerRow));

        foreach (var timestampGroup in records.GroupBy(record => record.TimestampUtcTicks).OrderBy(group => group.Key))
        {
            ct.ThrowIfCancellationRequested();

            var row = new List<string>
            {
                new DateTime(timestampGroup.Key, DateTimeKind.Utc)
                    .ToString("yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture)
            };
            row.AddRange(columns.Select(column =>
                timestampGroup.LastOrDefault(record => IsSameCsvColumn(record, column))?.Value ?? string.Empty));
            await writer.WriteLineAsync(CreateCsvRow(row));
        }
    }

    private async Task RunReplayAsync(CancellationToken ct)
    {
        SetState(CommunicationState.Running);
        var completedNaturally = false;
        var failed = false;
        try
        {
            await Task.Yield();
            StartLoadingData(ct);

            do
            {
                await ReplayRecordsAsync(ct);
                if (loop && !ct.IsCancellationRequested)
                {
                    ResetReplayPosition();
                }
            }
            while (loop && !ct.IsCancellationRequested);

            completedNaturally = !ct.IsCancellationRequested;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
        }
        catch (Exception e)
        {
            failed = true;
            Logger?.Log(LogLevel.Error, $"Replay failed: {e.Message}", e);
        }
        finally
        {
            SetState(CommunicationState.Stopped);
        }

        // Raise ReplayCompleted on failure as well — the replay ended either way and the UI
        // must not stay in the "playing" state forever. Only a user/host cancellation skips it.
        if (!completedNaturally && !failed)
        {
            return;
        }

        if (completedNaturally)
        {
            Logger?.Log(LogLevel.Info, "Replay reached end of data — raising ReplayCompleted.");
        }
        try
        {
            ReplayCompleted?.Invoke(this, EventArgs.Empty);
        }
        catch (Exception e)
        {
            Logger?.Log(LogLevel.Error, $"ReplayCompleted handler threw: {e.Message}", e);
        }
    }

    private async Task LoadReplayRecordsSafeAsync(CancellationToken ct)
    {
        try
        {
            await LoadReplayRecordsAsync(ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            if (completeDataLoadOnCancellation)
            {
                Logger?.Log(LogLevel.Info, "Replay data loading canceled.");
                CompleteReplayDataLoad();
            }
        }
        catch (Exception e)
        {
            Logger?.Log(LogLevel.Error, $"Replay data load failed: {e.Message}", e);
            CompleteReplayDataLoad();
        }
    }

    private async Task LoadReplayRecordsAsync(CancellationToken ct)
    {
        if (!File.Exists(logFilePath))
        {
            Logger?.Log(LogLevel.Error, $"Replay log file \"{logFilePath}\" not found.");
            CompleteReplayDataLoad();
            return;
        }

        ResetReplayDataLoadState();

        await using var stream = new FileStream(
            logFilePath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite,
            bufferSize: 4096,
            useAsync: true);

        while (stream.Position < stream.Length)
        {
            ct.ThrowIfCancellationRequested();
            var serializedRecord = await MessagePackSerializer.DeserializeAsync<SerializedVariableLogRecord>(
                stream,
                cancellationToken: ct);
            AddLoadedRecord(serializedRecord.ToDataLogRecord());
            RaiseDataLoadProgressChangedIfDue();
        }

        CompleteReplayDataLoad();
    }

    private void ResetReplayDataLoadState()
    {
        lock (replayStateLock)
        {
            replayRecords = [];
            replayRecordIndex = 0;
            replaySeekVersion++;
            replayFirstTimestampUtcTicks = 0;
            replayCurrentTimestampUtcTicks = 0;
            CurrentTime = TimeSpan.Zero;
            Duration = TimeSpan.Zero;
            lastDataLoadProgressTimestamp = 0;
            isDataLoaded = false;
            isDataLoading = true;
            lastLoadedTimestampUtcTicks = 0;
            loadedRecordsOutOfOrder = false;
        }

        RaiseReplayProgressChanged();
    }

    private void AddLoadedRecord(DataLogRecord record)
    {
        lock (replayStateLock)
        {
            if (replayRecords.Count == 0)
            {
                replayFirstTimestampUtcTicks = GetRecordTimestampUtcTicks(record);
                replayCurrentTimestampUtcTicks = replayFirstTimestampUtcTicks;
            }

            replayRecords.Add(record);
            var recordTimestampUtcTicks = GetRecordTimestampUtcTicks(record);
            if (recordTimestampUtcTicks < lastLoadedTimestampUtcTicks)
            {
                loadedRecordsOutOfOrder = true;
            }
            else
            {
                lastLoadedTimestampUtcTicks = recordTimestampUtcTicks;
            }
            Duration = recordTimestampUtcTicks > replayFirstTimestampUtcTicks
                ? TimeSpan.FromTicks(recordTimestampUtcTicks - replayFirstTimestampUtcTicks)
                : Duration;
        }

        replayRecordsAvailable.Release();
    }

    private void RaiseDataLoadProgressChangedIfDue()
    {
        var now = Stopwatch.GetTimestamp();
        if (lastDataLoadProgressTimestamp != 0
            && StopwatchTicksToTimeSpan(now - lastDataLoadProgressTimestamp) < DataLoadProgressUpdateInterval)
        {
            return;
        }

        lastDataLoadProgressTimestamp = now;
        RaiseReplayProgressChanged();
    }

    private void CompleteReplayDataLoad()
    {
        var reportOutOfOrder = false;
        lock (replayStateLock)
        {
            if (State != CommunicationState.Running && replayRecordIndex == 0)
            {
                replayRecords = replayRecords
                    .OrderBy(GetRecordTimestampUtcTicks)
                    .ToList();
                loadedRecordsOutOfOrder = false;
            }
            else if (loadedRecordsOutOfOrder && replayRecordIndex < replayRecords.Count)
            {
                // The file contained out-of-order records and replay is already consuming the
                // list, so indexes of the played prefix must not shift — sort only the part
                // that has not been replayed yet (keeps seek and pacing consistent from here on).
                var unplayed = replayRecords
                    .Skip(replayRecordIndex)
                    .OrderBy(GetRecordTimestampUtcTicks)
                    .ToList();
                replayRecords = replayRecords
                    .Take(replayRecordIndex)
                    .Concat(unplayed)
                    .ToList();
                reportOutOfOrder = true;
            }

            if (replayRecords.Count > 0)
            {
                replayFirstTimestampUtcTicks = GetRecordTimestampUtcTicks(replayRecords[0]);
                if (State != CommunicationState.Running && replayRecordIndex == 0)
                {
                    replayCurrentTimestampUtcTicks = replayFirstTimestampUtcTicks;
                    CurrentTime = TimeSpan.Zero;
                }

                var lastTimestampUtcTicks = GetRecordTimestampUtcTicks(replayRecords[^1]);
                Duration = lastTimestampUtcTicks > replayFirstTimestampUtcTicks
                    ? TimeSpan.FromTicks(lastTimestampUtcTicks - replayFirstTimestampUtcTicks)
                    : TimeSpan.Zero;
            }

            isDataLoaded = true;
            isDataLoading = false;
        }

        if (reportOutOfOrder)
        {
            Logger?.Log(
                LogLevel.Warn,
                $"Replay log file \"{Path.GetFileName(logFilePath)}\" contained out-of-order records; "
                + "the not-yet-replayed part was re-sorted.");
        }

        replayRecordsAvailable.Release();
        RaiseReplayProgressChanged();
    }

    private async Task ReplayRecordsAsync(CancellationToken ct)
    {
        var timingState = new ReplayTimingState();
        while (true)
        {
            if (ct.IsCancellationRequested)
            {
                break;
            }

            DataLogRecord? nextRecord;
            long currentTimestampUtcTicks;
            long nextRecordTimestampUtcTicks;
            int seekVersion;
            lock (replayStateLock)
            {
                if (replayRecordIndex >= replayRecords.Count)
                {
                    if (isDataLoaded)
                    {
                        break;
                    }

                    nextRecord = null!;
                    currentTimestampUtcTicks = 0;
                    nextRecordTimestampUtcTicks = 0;
                    seekVersion = replaySeekVersion;
                }
                else
                {
                    nextRecord = replayRecords[replayRecordIndex];
                    currentTimestampUtcTicks = replayCurrentTimestampUtcTicks;
                    nextRecordTimestampUtcTicks = GetRecordTimestampUtcTicks(nextRecord);
                    seekVersion = replaySeekVersion;
                }
            }

            if (nextRecord == null)
            {
                if (!await WaitForReplayRecordsAvailableAsync(ct))
                {
                    break;
                }

                continue;
            }

            var canContinue = await WaitUntilReplayRecordDueAsync(
                currentTimestampUtcTicks,
                nextRecordTimestampUtcTicks,
                seekVersion,
                timingState,
                ct);
            if (!canContinue)
            {
                continue;
            }

            if (!await WaitForReplayRecordGateAsync(ct))
            {
                break;
            }

            try
            {
                if (!IsCurrentSeekVersion(seekVersion))
                {
                    continue;
                }

                var records = GetDueReplayRecords(timingState, nextRecordTimestampUtcTicks);
                if (records.Count == 0)
                {
                    continue;
                }

                await PublishRecordsAsync(records, ct);
                lock (replayStateLock)
                {
                    if (!IsCurrentSeekVersionCore(seekVersion))
                    {
                        continue;
                    }

                    replayRecordIndex += records.Count;
                    replayCurrentTimestampUtcTicks = GetRecordTimestampUtcTicks(records[^1]);
                    CurrentTime = GetRelativeReplayTime(replayCurrentTimestampUtcTicks);
                }

                RaiseReplayProgressChanged();
            }
            finally
            {
                replayRecordGate.Release();
            }
        }
    }

    private async Task<bool> WaitUntilReplayRecordDueAsync(
        long currentTimestampUtcTicks,
        long nextRecordTimestampUtcTicks,
        int seekVersion,
        ReplayTimingState timingState,
        CancellationToken ct)
    {
        if (replayMode == ReplayMode.Immediate)
        {
            return await WaitWhilePausedAsync(seekVersion, ct);
        }

        if (timingState.SeekVersion != seekVersion)
        {
            timingState.SeekVersion = seekVersion;
            timingState.BaseTimestampUtcTicks = currentTimestampUtcTicks;
            timingState.BaseStopwatchTimestamp = Stopwatch.GetTimestamp();
        }

        while (!ct.IsCancellationRequested)
        {
            if (!IsCurrentSeekVersion(seekVersion))
            {
                return false;
            }

            if (IsPaused)
            {
                var pauseStartedTimestamp = Stopwatch.GetTimestamp();
                if (!await WaitWhilePausedAsync(seekVersion, ct))
                {
                    return false;
                }

                timingState.BaseStopwatchTimestamp += Stopwatch.GetTimestamp() - pauseStartedTimestamp;
                continue;
            }

            var dueTimestampUtcTicks = GetReplayDueTimestampUtcTicks(timingState);
            if (dueTimestampUtcTicks >= nextRecordTimestampUtcTicks)
            {
                return true;
            }

            var remainingTicks = (long)((nextRecordTimestampUtcTicks - dueTimestampUtcTicks) / speed);
            var delay = TimeSpan.FromTicks(Math.Max(1, remainingTicks));
            await Task.Delay(delay > TimeSpan.FromMilliseconds(50) ? TimeSpan.FromMilliseconds(50) : delay, ct);
        }

        return false;
    }

    private List<DataLogRecord> GetDueReplayRecords(ReplayTimingState timingState, long firstRecordTimestampUtcTicks)
    {
        lock (replayStateLock)
        {
            if (replayRecordIndex >= replayRecords.Count)
            {
                return [];
            }

            var dueTimestampUtcTicks = replayMode == ReplayMode.Immediate
                ? long.MaxValue
                : Math.Max(firstRecordTimestampUtcTicks, GetReplayDueTimestampUtcTicks(timingState));

            var records = new List<DataLogRecord>();
            for (var index = replayRecordIndex; index < replayRecords.Count; index++)
            {
                var record = replayRecords[index];
                if (GetRecordTimestampUtcTicks(record) > dueTimestampUtcTicks)
                {
                    break;
                }

                records.Add(record);
            }

            return records;
        }
    }

    private long GetReplayDueTimestampUtcTicks(ReplayTimingState timingState)
    {
        var elapsedStopwatchTicks = Stopwatch.GetTimestamp() - timingState.BaseStopwatchTimestamp;
        var elapsedReplayTicks = (long)(elapsedStopwatchTicks
            * (double)TimeSpan.TicksPerSecond
            / Stopwatch.Frequency
            * speed);

        return timingState.BaseTimestampUtcTicks + elapsedReplayTicks;
    }

    private async Task<bool> DelayBeforeReplayAsync(
        long previousTimestampUtcTicks,
        long currentTimestampUtcTicks,
        int seekVersion,
        CancellationToken ct)
    {
        if (replayMode == ReplayMode.Immediate)
        {
            return await WaitWhilePausedAsync(seekVersion, ct);
        }

        var ticks = currentTimestampUtcTicks - previousTimestampUtcTicks;
        if (ticks <= 0)
        {
            return await WaitWhilePausedAsync(seekVersion, ct);
        }

        var remainingDelay = TimeSpan.FromTicks((long)(ticks / speed));
        while (remainingDelay > TimeSpan.Zero)
        {
            if (!await WaitWhilePausedAsync(seekVersion, ct))
            {
                return false;
            }

            if (!IsCurrentSeekVersion(seekVersion))
            {
                return false;
            }

            var delay = remainingDelay > TimeSpan.FromMilliseconds(50)
                ? TimeSpan.FromMilliseconds(50)
                : remainingDelay;
            await Task.Delay(delay);
            remainingDelay -= delay;
        }

        return !ct.IsCancellationRequested && IsCurrentSeekVersion(seekVersion);
    }

    private async Task<bool> WaitWhilePausedAsync(int seekVersion, CancellationToken ct)
    {
        while (true)
        {
            if (ct.IsCancellationRequested)
            {
                return false;
            }

            if (!IsCurrentSeekVersion(seekVersion))
            {
                return false;
            }

            if (!IsPaused)
            {
                return true;
            }

            await Task.Delay(50);
        }
    }

    private async Task<bool> WaitForReplayRecordsAvailableAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            if (await replayRecordsAvailable.WaitAsync(TimeSpan.FromMilliseconds(50)))
            {
                return true;
            }
        }

        return false;
    }

    private async Task<bool> WaitForReplayRecordGateAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            if (await replayRecordGate.WaitAsync(TimeSpan.FromMilliseconds(50)))
            {
                return true;
            }
        }

        return false;
    }

    private async Task PublishRecordAsync(DataLogRecord record, CancellationToken ct)
    {
        await PublishRecordsAsync([record], ct);
    }

    private async Task PublishRecordsAsync(IEnumerable<DataLogRecord> records, CancellationToken ct)
    {
        foreach (var protocol in Protocols.OfType<ProtocolBase<DataLogRecord>>())
        {
            await protocol.AddReceivedDataToQueueAsync(records, ct);
        }
    }

    private void ResetReplayPosition()
    {
        lock (replayStateLock)
        {
            replayRecordIndex = 0;
            replayCurrentTimestampUtcTicks = replayFirstTimestampUtcTicks;
            CurrentTime = TimeSpan.Zero;
            replaySeekVersion++;
        }

        RaiseReplayProgressChanged();
    }

    private int FindFirstRecordIndexAfter(long timestampUtcTicks)
    {
        var low = 0;
        var high = replayRecords.Count;
        while (low < high)
        {
            var mid = low + ((high - low) / 2);
            if (GetRecordTimestampUtcTicks(replayRecords[mid]) <= timestampUtcTicks)
            {
                low = mid + 1;
            }
            else
            {
                high = mid;
            }
        }

        return low;
    }

    private List<DataLogRecord> GetRecordsUpTo(long timestampUtcTicks)
    {
        var records = new List<DataLogRecord>();
        foreach (var record in replayRecords)
        {
            if (GetRecordTimestampUtcTicks(record) > timestampUtcTicks)
            {
                break;
            }

            records.Add(record);
        }

        return records;
    }

    private TimeSpan GetRelativeReplayTime(long timestampUtcTicks)
    {
        if (replayFirstTimestampUtcTicks == 0 || timestampUtcTicks <= replayFirstTimestampUtcTicks)
        {
            return TimeSpan.Zero;
        }

        return TimeSpan.FromTicks(timestampUtcTicks - replayFirstTimestampUtcTicks);
    }

    private bool IsCurrentSeekVersion(int seekVersion)
    {
        lock (replayStateLock)
        {
            return IsCurrentSeekVersionCore(seekVersion);
        }
    }

    private bool IsCurrentSeekVersionCore(int seekVersion)
    {
        return replaySeekVersion == seekVersion;
    }

    private void RaiseReplayProgressChanged()
    {
        ReplayProgressChanged?.Invoke(
            this,
            new ReplayProgressChangedEventArgs(CurrentTime, Duration, IsPaused, IsDataLoaded));
    }

    private static long GetRecordTimestampUtcTicks(DataLogRecord record)
    {
        return record.TimestampUtcTicks > 0 ? record.TimestampUtcTicks : 0;
    }

    private static TimeSpan StopwatchTicksToTimeSpan(long stopwatchTicks)
    {
        return TimeSpan.FromTicks((long)(stopwatchTicks * (double)TimeSpan.TicksPerSecond / Stopwatch.Frequency));
    }

    private sealed class ReplayTimingState
    {
        public int SeekVersion { get; set; } = -1;
        public long BaseTimestampUtcTicks { get; set; }
        public long BaseStopwatchTimestamp { get; set; }
    }

    private static List<CsvColumn> CreateCsvColumns(IEnumerable<DataLogRecord> records)
    {
        var columns = new List<CsvColumn>();
        var usedHeaders = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var record in records)
        {
            if (columns.Any(column => IsSameCsvColumn(record, column)))
            {
                continue;
            }

            var header = GetCsvColumnHeader(record);
            if (!usedHeaders.Add(header))
            {
                header = $"{header} ({record.VariableId})";
                usedHeaders.Add(header);
            }

            columns.Add(new CsvColumn(record.VariableId, record.VariableNamespace, record.VariableName, header));
        }

        return columns;
    }

    private static bool IsSameCsvColumn(DataLogRecord record, CsvColumn column)
    {
        return record.VariableId == column.VariableId
               && record.VariableNamespace.Equals(column.VariableNamespace, StringComparison.Ordinal)
               && record.VariableName.Equals(column.VariableName, StringComparison.Ordinal);
    }

    private static string GetCsvColumnHeader(DataLogRecord record)
    {
        if (!string.IsNullOrWhiteSpace(record.VariableLabel))
        {
            return record.VariableLabel;
        }

        if (!string.IsNullOrWhiteSpace(record.VariableName))
        {
            return record.VariableName;
        }

        if (!string.IsNullOrWhiteSpace(record.VariableNamespace))
        {
            return record.VariableNamespace;
        }

        return $"Variable {record.VariableId}";
    }

    private static string CreateCsvRow(IEnumerable<string> values)
    {
        return string.Join(',', values.Select(EscapeCsvValue));
    }

    private static string EscapeCsvValue(string value)
    {
        if (value.IndexOfAny(['"', ',', '\r', '\n']) < 0)
        {
            return value;
        }

        return $"\"{value.Replace("\"", "\"\"")}\"";
    }

    private enum ReplayMode
    {
        Realtime,
        Immediate
    }

    private sealed record CsvColumn(int VariableId, string VariableNamespace, string VariableName, string Header);
}
