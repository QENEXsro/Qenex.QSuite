using System.Net.Sockets;
using Qenex.QSuite.LogSystems.LogSystem;

namespace Qenex.QSuite.Drivers.Driver;

/// <summary>
/// Shared TCP keepalive setting of the TCP drivers (client and server). A pulled cable or a
/// vanished peer produces no packet, so without probes a quiet connection looks alive until the
/// operating system's own retransmission timeout expires (15-20 s or more). With keepalive the
/// OS probes the idle connection itself: after <c>keepAliveMs</c> of silence it sends a probe
/// every second and closes the connection after three unanswered probes, so the driver learns
/// about a dead link within a few seconds regardless of what the protocol is doing.
/// <c>keepAliveMs=0</c> switches the probes off for networks that must not see them.
/// </summary>
public static class TcpKeepAlive
{
    public const string Key = "keepAliveMs";
    public const int DefaultMs = 5000;

    private const int ProbeIntervalSeconds = 1;
    private const int ProbeCount = 3;

    /// <summary>Reads <c>keepAliveMs</c> (0 = off); an unparsable or negative value falls back to the default.</summary>
    public static int Parse(IReadOnlyDictionary<string, string> settings, ILogger? logger, string driverName)
    {
        if (!settings.TryGetValue(Key, out var value))
        {
            return DefaultMs;
        }

        if (int.TryParse(value, out var parsed) && parsed >= 0)
        {
            return parsed;
        }

        logger?.Log(LogLevel.Warn, $"{driverName}: invalid value '{value}' for setting '{Key}', using {DefaultMs}.");
        return DefaultMs;
    }

    /// <summary>Applies the setting to a connected socket; 0 leaves keepalive off.</summary>
    public static void Apply(Socket socket, int keepAliveMs)
    {
        if (keepAliveMs <= 0)
        {
            socket.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.KeepAlive, false);
            return;
        }

        socket.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.KeepAlive, true);
        socket.SetSocketOption(SocketOptionLevel.Tcp, SocketOptionName.TcpKeepAliveTime, Math.Max(1, keepAliveMs / 1000));
        socket.SetSocketOption(SocketOptionLevel.Tcp, SocketOptionName.TcpKeepAliveInterval, ProbeIntervalSeconds);
        socket.SetSocketOption(SocketOptionLevel.Tcp, SocketOptionName.TcpKeepAliveRetryCount, ProbeCount);
    }

    /// <summary>Text for the settings template of a driver.</summary>
    public static string SettingsTemplate() => $"{Key}={DefaultMs}";
}
