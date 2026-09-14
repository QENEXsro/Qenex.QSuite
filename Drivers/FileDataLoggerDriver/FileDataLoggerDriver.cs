using System.Diagnostics;
using System.Globalization;
using System.Reflection;
using MessagePack;
using Qenex.QSuite.Common.CoreComm;
using Qenex.QSuite.Drivers.Driver;
using Qenex.QSuite.LogSystems.LogSystem;
using Qenex.QSuite.Protocols.Protocol;
using Qenex.QSuite.Specifications.Specification;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Win32;

namespace Qenex.QSuite.Drivers.FileDataLoggerDriver;

public class FileDataLoggerDriver : DriverBase, IProtocolVariableSinkDriver, IDataLogFileNameDriver, ITransportSource<IProtocolVariable>
{
    private const string DataLogExtension = ".qilog";
    private const string TimestampFormat = "yyyyMMdd'_'HH'h'mm";
    private static readonly TimeSpan DefaultReorderBufferDelay = TimeSpan.FromMilliseconds(500);
    // With flushOnWrite=false the FileStream buffers up to 4 KB in-process; flush when the
    // writer goes idle so an application crash costs at most ~1 s of data, not the whole tail.
    private static readonly TimeSpan IdleFlushInterval = TimeSpan.FromSeconds(1);
    private readonly object pendingRecordsLock = new();
    private readonly LinkedList<BufferedVariableLogRecord> pendingRecords = [];
    private readonly EventWaitHandle waitHandle = new AutoResetEvent(false);
    private string logFilePath = Path.Combine(DriverEnvironment.DataRootDirectory, "DataLogs");
    private bool append = true;
    private bool flushOnWrite;
    private TimeSpan reorderBufferDelay = DefaultReorderBufferDelay;
    private long highestBufferedTimestampUtcTicks;
    private volatile bool exitRequested;
    private FileStream? logStream;
    private Task? writerTask;
    private bool logStreamDirty;
    private long lastIdleFlushTimestamp;

    public FileDataLoggerDriver()
    {
        Specification = new SpecificationBase
        {
            Name = "FileDataLoggerDriver",
            Label = "Data Log Recorder",
            Description = "Records selected variable values into a .qilog file.",
            CreatedOn = new DateTime(2026, 5, 25),
            Version = Assembly.GetExecutingAssembly().GetName().Version ?? new Version(1, 0, 0),
            Author = "Qenex",
            Company = "QENEX Ltd."
        };
    }

    public string? DataLogFileName { get; set; }

    public override string DefaultRawSettings => "file=DataLogs;append=true;flushOnWrite=false;reorderBufferMs=500";

    private static readonly string[] KnownSettings = ["file", "directory", "append", "flushOnWrite", "reorderBufferMs"];

    public override void SetConfiguration()
    {
        logFilePath = Path.Combine(DriverEnvironment.DataRootDirectory, "DataLogs");
        append = true;
        flushOnWrite = false;
        reorderBufferDelay = DefaultReorderBufferDelay;

        var settings = SettingsParser.Parse(RawSettings, KnownSettings, Logger, "Data log recorder driver");
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

        if (settings.TryGetValue("append", out var appendValue))
        {
            if (bool.TryParse(appendValue, out var parsedAppend))
            {
                append = parsedAppend;
            }
            else
            {
                Logger?.Log(LogLevel.Warn, $"Data logger: invalid append value '{appendValue}', using {append}.");
            }
        }

        if (settings.TryGetValue("flushOnWrite", out var flushValue))
        {
            if (bool.TryParse(flushValue, out var parsedFlush))
            {
                flushOnWrite = parsedFlush;
            }
            else
            {
                Logger?.Log(LogLevel.Warn, $"Data logger: invalid flushOnWrite value '{flushValue}', using {flushOnWrite}.");
            }
        }

        if (settings.TryGetValue("reorderBufferMs", out var reorderBufferValue))
        {
            if (double.TryParse(reorderBufferValue, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsedReorderBufferMs)
                && parsedReorderBufferMs >= 0)
            {
                reorderBufferDelay = TimeSpan.FromMilliseconds(parsedReorderBufferMs);
            }
            else
            {
                Logger?.Log(LogLevel.Warn, $"Data logger: invalid reorderBufferMs value '{reorderBufferValue}', using {reorderBufferDelay.TotalMilliseconds}.");
            }
        }
    }

    public bool CanSubscribe(IProtocolVariable sourceVariable)
    {
        return Protocols
            .OfType<IProtocolVariableSinkProtocol>()
            .Any(protocol => protocol.CanProcess(sourceVariable));
    }

    public Task OnProtocolVariableValueChangedAsync(IProtocolVariable sourceVariable)
    {
        // Driver is intentionally off (typically during replay / when logging is not enabled) —
        // recording is not expected, so do not report a "dropout" (would be a false alarm).
        if (!IsEnabled || State == CommunicationState.Disabled)
        {
            return Task.CompletedTask;
        }

        if (State != CommunicationState.Running || logStream == null || !CanSubscribe(sourceVariable))
        {
            return Task.CompletedTask;
        }

        try
        {
            var record = CreateRecord(sourceVariable);
            BufferRecord(record);
            waitHandle.Set();
        }
        catch (Exception e)
        {
            Logger?.Log(
                LogLevel.Error,
                $"FileDataLogger failed to buffer a record for '{sourceVariable.Variable?.Name}': {e.Message}",
                e);
        }

        return Task.CompletedTask;
    }

    public override Task StartAsync(CancellationToken ct = default)
    {
        if (!IsEnabled)
        {
            SetState(CommunicationState.Disabled);
            return Task.CompletedTask;
        }
        if (State == CommunicationState.Running) return Task.CompletedTask;


        if (!SinkSealValid())
        {
            // Disabled (ne Stopped): OnProtocolVariableValueChangedAsync ma early-return na
            // State == Disabled, takze prichozi hodnoty netriggeruji skip-diagnostiku (zadny log).
            // Zaroven splyva se stavem "logger je vypnuty". Hodnoty se neukladaji.
            SetState(CommunicationState.Disabled);
            return Task.CompletedTask;
        }

        SetState(CommunicationState.Starting);
        foreach (var protocol in Protocols)
        {
            _ = protocol.StartAsync(ct);
        }

        var timestampedLogFilePath = CreateTimestampedLogFilePath(logFilePath, DataLogFileName, DateTime.Now);
        var directory = Path.GetDirectoryName(timestampedLogFilePath);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        logStream = new FileStream(
            timestampedLogFilePath,
            append ? FileMode.Append : FileMode.Create,
            FileAccess.Write,
            FileShare.Read,
            bufferSize: 4096,
            useAsync: true);
        

        exitRequested = false;
        highestBufferedTimestampUtcTicks = 0;
        logStreamDirty = false;
        lastIdleFlushTimestamp = 0;
        writerTask = RunWriterLoopAsync();
        SetState(CommunicationState.Running);
        return Task.CompletedTask;
    }

    public override async Task StopAsync(CancellationToken ct = default)
    {
        SetState(CommunicationState.Stopping);
        exitRequested = true;
        waitHandle.Set();

        if (writerTask != null)
        {
            await writerTask.WaitAsync(ct);
            writerTask = null;
        }

        if (logStream != null)
        {
            await logStream.FlushAsync(ct);
            await logStream.DisposeAsync();
            logStream = null;
        }

        foreach (var protocol in Protocols)
        {
            await protocol.StopAsync(ct);
        }

        SetState(CommunicationState.Stopped);
    }

    public override void Dispose()
    {
        exitRequested = true;
        waitHandle.Set();
        writerTask?.Wait(TimeSpan.FromSeconds(1));
        logStream?.Dispose();
        waitHandle.Dispose();
    }

    public override void Send<T>(T data)
    {
        throw new NotSupportedException("File data logger driver does not send data.");
    }

    public override Task SendAsync<T>(T data, CancellationToken ct = default)
    {
        throw new NotSupportedException("File data logger driver does not send data.");
    }

    private async Task RunWriterLoopAsync()
    {
        await Task.Run(async () =>
        {
            try
            {
                while (!exitRequested || HasPendingRecords())
                {
                    var record = TryTakeWritableRecord();
                    if (record != null)
                    {
                        await WriteRecordAsync(record);
                    }
                    else
                    {
                        await FlushIfDueAsync();
                        waitHandle.WaitOne(TimeSpan.FromMilliseconds(20));
                    }
                }
            }
            catch (Exception e)
            {
                // If the writer loop crashes, recording silently ends until Stop -> report it.
                Logger?.Log(
                    LogLevel.Error,
                    $"FileDataLogger writer loop terminated unexpectedly — recording has stopped: {e.Message}",
                    e);
            }
        });
    }

    private void BufferRecord(VariableLogRecord record)
    {
        var bufferedRecord = new BufferedVariableLogRecord(record);
        lock (pendingRecordsLock)
        {
            if (record.TimestampUtcTicks > highestBufferedTimestampUtcTicks)
            {
                highestBufferedTimestampUtcTicks = record.TimestampUtcTicks;
            }

            if (pendingRecords.Last == null)
            {
                pendingRecords.AddLast(bufferedRecord);
                return;
            }

            var node = pendingRecords.Last;
            while (node != null && node.Value.Record.TimestampUtcTicks > record.TimestampUtcTicks)
            {
                node = node.Previous;
            }

            if (node == null)
            {
                pendingRecords.AddFirst(bufferedRecord);
            }
            else
            {
                pendingRecords.AddAfter(node, bufferedRecord);
            }
        }
    }

    private bool HasPendingRecords()
    {
        lock (pendingRecordsLock)
        {
            return pendingRecords.Count > 0;
        }
    }

    private VariableLogRecord? TryTakeWritableRecord()
    {
        lock (pendingRecordsLock)
        {
            var first = pendingRecords.First;
            if (first == null)
            {
                return null;
            }

            if (!exitRequested && !CanWriteBufferedRecord(first.Value))
            {
                return null;
            }

            pendingRecords.RemoveFirst();
            return first.Value.Record;
        }
    }

    private bool CanWriteBufferedRecord(BufferedVariableLogRecord bufferedRecord)
    {
        if (reorderBufferDelay <= TimeSpan.Zero)
        {
            return true;
        }

        var timestampDistance = highestBufferedTimestampUtcTicks - bufferedRecord.Record.TimestampUtcTicks;
        if (timestampDistance >= reorderBufferDelay.Ticks)
        {
            return true;
        }

        return false;
    }

    private async Task WriteRecordAsync(VariableLogRecord record)
    {
        if (logStream == null)
        {
            // The record was already removed from the buffer -> it used to be lost silently here. Report it.
            Logger?.Log(LogLevel.Warn, "FileDataLogger dropped a record because the log stream was closed.");
            return;
        }

        try
        {
            await MessagePackSerializer.SerializeAsync(logStream, record);
            if (flushOnWrite)
            {
                await logStream.FlushAsync();
            }
            else
            {
                logStreamDirty = true;
            }
        }
        catch (Exception e)
        {
            Logger?.Log(LogLevel.Error, $"Data log record could not be written: {e.Message}");
        }
    }

    private async Task FlushIfDueAsync()
    {
        if (flushOnWrite || !logStreamDirty || logStream == null)
        {
            return;
        }

        var now = Stopwatch.GetTimestamp();
        if (lastIdleFlushTimestamp != 0
            && Stopwatch.GetElapsedTime(lastIdleFlushTimestamp, now) < IdleFlushInterval)
        {
            return;
        }

        lastIdleFlushTimestamp = now;
        try
        {
            await logStream.FlushAsync();
            logStreamDirty = false;
        }
        catch (Exception e)
        {
            Logger?.Log(LogLevel.Error, $"Data log flush failed: {e.Message}");
        }
    }

    private static VariableLogRecord CreateRecord(IProtocolVariable protocolVariable)
    {
        var variable = protocolVariable.Variable;
        var value = variable.GetValue();
        return new VariableLogRecord
        {
            TimestampUtcTicks = variable.Timestamp.ToUniversalTime().Ticks,
            VariableId = variable.Id,
            VariableNamespace = variable.Namespace,
            VariableName = variable.Name,
            VariableLabel = variable.Label,
            ValueType = value?.GetType().FullName ?? string.Empty,
            Value = ConvertValue(value)
        };
    }

    private static string ConvertValue(object? value)
    {
        return value switch
        {
            null => string.Empty,
            IFormattable formattable => formattable.ToString(null, CultureInfo.InvariantCulture),
            _ => value.ToString() ?? string.Empty
        };
    }

    private static string CreateTimestampedLogFilePath(string configuredFilePath, string? dataLogFileName, DateTime timestamp)
    {
        var hasFileExtension = !string.IsNullOrWhiteSpace(Path.GetExtension(configuredFilePath));
        var directory = hasFileExtension ? Path.GetDirectoryName(configuredFilePath) : configuredFilePath;
        var fileName = !string.IsNullOrWhiteSpace(dataLogFileName)
            ? dataLogFileName
            : hasFileExtension
                ? Path.GetFileNameWithoutExtension(configuredFilePath)
                : null;
        if (string.IsNullOrWhiteSpace(fileName))
        {
            fileName = "values";
        }

        var timestampText = timestamp.ToString(TimestampFormat, CultureInfo.InvariantCulture);
        var timestampedFileName = $"{fileName}_{timestampText}{DataLogExtension}";
        return string.IsNullOrWhiteSpace(directory)
            ? timestampedFileName
            : Path.Combine(directory, timestampedFileName);
    }

    private sealed record BufferedVariableLogRecord(VariableLogRecord Record);

    // LICENSE-SEAL COPY v1 — sync z _LicenseGuard/LicenseSealTemplate.cs
    // Nezavisly licencni check (necte licenseService). true=licencovano/nejasne (fail-open),
    // false=jiste nelicencovano. Reakce (nenastartovat) je v StartAsync.
    [System.Reflection.Obfuscation(Feature = "code control flow obfuscation", Exclude = false)]
    private static bool SinkSealValid()
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
