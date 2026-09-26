// Live matrix test: the real QSuite master stack (TcpClientDriver + XcpTcp, block mode) against the
// QFW SDK engine in Qenex.QFirmware/tests/engine/pc_server.exe over TCP loopback (or a real ECU):
// matrix poll, On Request read, cell writes merged into windows, read-back, timing of 1 KB and 16 KB
// blocks. Usage: XcpMatrixLiveTest <port> <mapAddr> <curveAddr> <bigAddr> (addresses printed by pc_server).
using System.Diagnostics;
using System.Globalization;
using Qenex.QSuite.Common.CoreComm;
using Qenex.QSuite.Drivers.TcpClientDriver;
using Qenex.QSuite.LogSystems.LogSystem;
using Qenex.QSuite.Protocols.XcpTcpProtocol;
using Qenex.QSuite.Variables.QVariables;
using Qenex.QSuite.Variables.VariableEvents;
using ValueDataType = Qenex.QSuite.Variables.QVariables.Values.ValuesGlobal.ValueDataType;

internal static class Program
{
    private static int failures;

    private static void Check(bool condition, string what)
    {
        Console.WriteLine($"{(condition ? "ok  " : "FAIL")} {what}");
        if (!condition) failures++;
    }

    private static async Task<int> Main(string[] args)
    {
        if (args.Length < 4)
        {
            Console.WriteLine("usage: XcpMatrixE2E <port> <mapAddr> <curveAddr> <bigAddr>");
            return 2;
        }

        var port = int.Parse(args[0], CultureInfo.InvariantCulture);
        var logger = new ConsoleLogger();
        var driver = new TcpClientDriver
        {
            Label = "pc_server", IsEnabled = true, Logger = logger,
            RawSettings = $"ip=127.0.0.1;port={port};connectionTimeoutMs=3000;reconnectDelayMs=1000;reconnectAttempts=2;keepAliveMs=5000"
        };
        driver.SetConfiguration();
        var protocol = new XcpTcp { IsEnabled = true, Logger = logger, RawSettings = "requestTimeoutMs=1000" };
        protocol.SetConfiguration();
        driver.AddProtocol(protocol);

        IVarEvent[] events = [new OnRequestVarEvent { Name = "onRequest" }, new PeriodicVarEvent { Name = "poll500", Period = 500, Unit = TimeUnit.Milisec }];
        var map = new MatrixVariable
        {
            Id = 1, Namespace = "/", Name = "DemoMap", Label = "Demo map", DefaultDataType = ValueDataType.Float,
            XAxis = new MatrixSection { Count = 16, DataType = ValueDataType.UShort },
            YAxis = new MatrixSection { Count = 16, DataType = ValueDataType.Byte },
            Data = new MatrixSection()
        };
        var curve = new MatrixVariable
        {
            Id = 2, Namespace = "/", Name = "DemoCurve", Label = "Demo curve", DefaultDataType = ValueDataType.UShort,
            XAxis = new MatrixSection { Count = 8 }, Data = new MatrixSection()
        };
        var big = new MatrixVariable
        {
            Id = 3, Namespace = "/", Name = "DemoBigMap", Label = "Demo big map", DefaultDataType = ValueDataType.Float,
            XAxis = new MatrixSection { Count = 64, DataType = ValueDataType.UShort },
            YAxis = new MatrixSection { Count = 64, DataType = ValueDataType.UShort },
            Data = new MatrixSection()
        };
        var mapPv = protocol.CreateProtocolVariable(map, events, $"address=\"{args[1]}\";direction=\"readWrite\";eventRef=\"onRequest\"", true)!;
        var curvePv = protocol.CreateProtocolVariable(curve, events, $"address=\"{args[2]}\";direction=\"read\";eventRef=\"poll500\"", true)!;
        var bigPv = protocol.CreateProtocolVariable(big, events, $"address=\"{args[3]}\";direction=\"readWrite\";eventRef=\"onRequest\"", true)!;
        Check(mapPv != null && curvePv != null && bigPv != null, "protocol variables created (map 1072 B, curve 32 B, big map 16640 B)");
        protocol.AddVariable(mapPv);
        protocol.AddVariable(curvePv);
        protocol.AddVariable(bigPv);

        await driver.StartAsync();
        var connected = await WaitUntilAsync(() => protocol.State == CommunicationState.Running, 8000);
        Check(connected, $"session running ({protocol.State}: {protocol.StateMessage})");
        if (!connected)
        {
            await driver.StopAsync();
            return 1;
        }

        // On Request map: 1 KB block
        Check(protocol.CanReadVariable(mapPv) && protocol.CanReadVariable(bigPv) && !protocol.CanReadVariable(curvePv), "CanRead: On Request matrices yes, polled curve no");
        var sw = Stopwatch.StartNew();
        await protocol.ReadVariableAsync(mapPv);
        var mapMs = sw.Elapsed.TotalMilliseconds;
        Check(map.GetEngValue(MatrixSectionKind.XAxis, 3) == 103 && map.GetEngValue(MatrixSectionKind.YAxis, 7) == 7 &&
              map.GetEngValue(MatrixSectionKind.Data, 5 * 16 + 9) == 89, $"map read on request: axes + data decoded ({mapMs:F1} ms for 1072 B)");
        Check(mapMs < 50, "map read under 50 ms (target for TCP)");

        // polled curve
        await Task.Delay(1200);
        Check(curve.GetEngValue(MatrixSectionKind.XAxis, 7) == 2750 && curve.GetEngValue(MatrixSectionKind.Data, 3) == 80, "curve polled on the periodic event (axis + data)");

        // 16 KB map (the agreed limit)
        sw.Restart();
        await protocol.ReadVariableAsync(bigPv);
        var bigMs = sw.Elapsed.TotalMilliseconds;
        Check(big.GetEngValue(MatrixSectionKind.XAxis, 10) == 1500 && big.GetEngValue(MatrixSectionKind.YAxis, 63) == 126 &&
              Math.Abs(big.GetEngValue(MatrixSectionKind.Data, 63 * 64 + 63) - 2047.5) < 1e-6, $"64x64 map read on request ({bigMs:F1} ms for 16640 B)");
        Check(bigMs < 200, "16 KB map read under 200 ms on TCP loopback");

        // cell writes: two adjacent data cells + one axis cell -> two windows; nothing echoed, read-back shows them
        EditCell(map, MatrixSectionKind.Data, 5 * 16 + 9, 1234.5);
        EditCell(map, MatrixSectionKind.Data, 5 * 16 + 10, 2345.5);
        EditCell(map, MatrixSectionKind.XAxis, 3, 333);
        sw.Restart();
        await protocol.WriteVariableAsync(mapPv);
        var writeMs = sw.Elapsed.TotalMilliseconds;
        // overwrite local memory to prove the read-back comes from the device
        map.TrySetEngValue(MatrixSectionKind.Data, 5 * 16 + 9, 0);
        await protocol.ReadVariableAsync(mapPv);
        Check(map.GetEngValue(MatrixSectionKind.Data, 5 * 16 + 9) == 1234.5 && map.GetEngValue(MatrixSectionKind.Data, 5 * 16 + 10) == 2345.5 &&
              map.GetEngValue(MatrixSectionKind.XAxis, 3) == 333 && map.GetEngValue(MatrixSectionKind.Data, 5 * 16 + 11) == 91,
            $"cell writes landed in the device and read back ({writeMs:F1} ms for 2 windows), neighbour untouched");

        // big map: a whole row of 64 floats (256 B, one window = one block + a 1 B block) and read-back
        for (var i = 0; i < 64; i++)
        {
            EditCell(big, MatrixSectionKind.Data, 20 * 64 + i, 7000 + i);
        }
        sw.Restart();
        await protocol.WriteVariableAsync(bigPv);
        var bigWriteMs = sw.Elapsed.TotalMilliseconds;
        await protocol.ReadVariableAsync(bigPv);
        var rowOk = true;
        for (var i = 0; i < 64; i++)
        {
            rowOk &= big.GetEngValue(MatrixSectionKind.Data, 20 * 64 + i) == 7000 + i;
        }
        Check(rowOk && Math.Abs(big.GetEngValue(MatrixSectionKind.Data, 21 * 64) - 21 * 64 * 0.5) < 1e-6, $"64-cell row written as one 256 B window and read back ({bigWriteMs:F1} ms)");

        await driver.StopAsync();
        Console.WriteLine(failures == 0 ? "E2E PASSED" : $"E2E FAILED ({failures})");
        return failures == 0 ? 0 : 1;
    }

    private static void EditCell(MatrixVariable matrix, MatrixSectionKind kind, int index, double engValue)
    {
        if (!matrix.TrySetEngValue(kind, index, engValue))
        {
            Check(false, $"edit {kind}[{index}]");
            return;
        }

        var elementSize = matrix.GetElementSize(kind);
        var byteOffset = matrix.GetSectionOffset(kind) + index * elementSize;
        matrix.EnqueuePendingWrite(new MatrixWriteRequest(byteOffset, matrix.RawData.AsSpan(byteOffset, elementSize).ToArray()));
    }

    private static async Task<bool> WaitUntilAsync(Func<bool> condition, int timeoutMs)
    {
        var deadline = Environment.TickCount64 + timeoutMs;
        while (Environment.TickCount64 < deadline)
        {
            if (condition()) return true;
            await Task.Delay(20);
        }

        return condition();
    }

    private sealed class ConsoleLogger : ILogger
    {
        public void RegisterSubscriber(ILogSubscriber subscriber) { }
        public void UnRegisterSubscriber(ILogSubscriber subscriber) { }
        public void Log(ILogMessage message) { }
        public void Log(LogLevel level, string message, Exception? exception = default)
        {
            if (level >= LogLevel.Info) Console.WriteLine($"  [{level}] {message}");
        }
        public Task LogAsync(ILogMessage message, CancellationToken ct) => Task.CompletedTask;
        public Task LogAsync(LogLevel level, string message, Exception? exception = default, CancellationToken ct = default)
        {
            Log(level, message, exception);
            return Task.CompletedTask;
        }
        public void Enable() { }
        public void Disable() { }
    }
}
