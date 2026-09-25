using Qenex.QSuite.LogSystems.LogSystem;

namespace Qenex.QSuite.Tests.XcpProtocolTest;

/// <summary>Collects protocol log lines so tests can assert on level and wording.</summary>
internal sealed class CapturingLogger : ILogger
{
    public readonly List<(LogLevel Level, string Message)> Entries = [];

    public bool Has(LogLevel level, string fragment)
    {
        lock (Entries)
        {
            return Entries.Any(e => e.Level == level && e.Message.Contains(fragment, StringComparison.Ordinal));
        }
    }

    public bool HasAny(LogLevel level)
    {
        lock (Entries)
        {
            return Entries.Any(e => e.Level == level);
        }
    }

    public void RegisterSubscriber(ILogSubscriber subscriber) { }
    public void UnRegisterSubscriber(ILogSubscriber subscriber) { }
    public void Log(ILogMessage message) { }

    public void Log(LogLevel level, string message, Exception? exception = default)
    {
        lock (Entries)
        {
            Entries.Add((level, message));
        }
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

/// <summary>Console-style unit tests for the XCP protocol (repo convention, same as the other
/// _Tests projects). Exit code 0 = all passed.</summary>
internal static class Program
{
    private static int failures;

    private static int Main()
    {
        SpecificationTests.Run();
        CodecTests.Run();
        MasterTests.Run();
        DaqTests.Run();
        StimTests.Run();
        EthernetFramerTests.Run();
        IntegrationTests.Run();
        TcpIntegrationTests.Run();
        TcpDaqIntegrationTests.Run();
        TcpStimIntegrationTests.Run();
        BlockModeTests.Run();
        MatrixIntegrationTests.Run();

        Console.WriteLine(failures == 0 ? "ALL TESTS PASSED" : $"{failures} TEST(S) FAILED");
        return failures == 0 ? 0 : 1;
    }

    internal static void Check(bool condition, string description)
    {
        if (condition)
        {
            return;
        }

        failures++;
        Console.WriteLine($"FAILED: {description}");
    }

    internal static void CheckThrows<TException>(Action action, string description) where TException : Exception
    {
        try
        {
            action();
            Check(false, $"{description} (no exception thrown)");
        }
        catch (TException)
        {
            Check(true, description);
        }
        catch (Exception e)
        {
            Check(false, $"{description} (threw {e.GetType().Name} instead of {typeof(TException).Name})");
        }
    }
}
