using System.Net.Sockets;

namespace AgenticMemory.Http.Optimizations;

/// <summary>
/// Socket configuration for optimal TCP performance
/// </summary>
public static class SocketOptimizations
{
    /// <summary>
    /// Configure TcpListener for high-performance scenarios
    /// </summary>
    public static void ConfigureListener(TcpListener listener)
    {
        // Allow address reuse for fast server restarts
        listener.Server.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);

        // Increase listen backlog for burst traffic
        // Default is typically 128, we increase for high concurrency
        listener.Server.Listen(2048);
    }

    /// <summary>
    /// Configure TcpClient for low-latency communication
    /// </summary>
    public static void ConfigureClient(TcpClient client)
    {
        var socket = client.Client;

        // Disable Nagle's algorithm for low latency
        // This sends data immediately without waiting to batch small packets
        socket.NoDelay = true;

        // Set send buffer size (64KB for good throughput)
        socket.SendBufferSize = 65536;

        // Set receive buffer size (64KB)
        socket.ReceiveBufferSize = 65536;

        // Enable TCP keep-alive to detect dead connections
        socket.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.KeepAlive, true);

        // Configure keep-alive timing (Linux/Windows)
        // Send keepalive after 60 seconds of inactivity
        // Then every 10 seconds, up to 3 retries
        try
        {
            // Windows-specific keep-alive configuration
            if (OperatingSystem.IsWindows())
            {
                // tcp_keepidle = 60 seconds, tcp_keepintvl = 10 seconds
                var keepAliveValues = new byte[12];
                BitConverter.TryWriteBytes(keepAliveValues.AsSpan(0, 4), 1);      // Enable
                BitConverter.TryWriteBytes(keepAliveValues.AsSpan(4, 4), 60000);  // Time (ms)
                BitConverter.TryWriteBytes(keepAliveValues.AsSpan(8, 4), 10000);  // Interval (ms)
                socket.IOControl(IOControlCode.KeepAliveValues, keepAliveValues, null);
            }
        }
        catch
        {
            // Ignore if keep-alive configuration fails (not critical)
        }

        // Set linger option to ensure data is sent before close
        socket.LingerState = new LingerOption(true, 5);
    }

    /// <summary>
    /// Configure socket for maximum throughput (benchmarking)
    /// </summary>
    public static void ConfigureForThroughput(TcpClient client)
    {
        ConfigureClient(client);

        var socket = client.Client;

        // Increase buffer sizes for throughput
        socket.SendBufferSize = 131072;    // 128KB
        socket.ReceiveBufferSize = 131072;  // 128KB
    }

    /// <summary>
    /// Configure socket for minimum latency (real-time)
    /// </summary>
    public static void ConfigureForLatency(TcpClient client)
    {
        ConfigureClient(client);

        var socket = client.Client;

        // Smaller buffers for lower latency
        socket.SendBufferSize = 8192;     // 8KB
        socket.ReceiveBufferSize = 8192;   // 8KB

        // Ensure NoDelay is set
        socket.NoDelay = true;
    }
}

/// <summary>
/// Connection metrics for monitoring
/// </summary>
public sealed class ConnectionMetrics
{
    private long _totalConnections;
    private long _activeConnections;
    private long _totalRequests;
    private long _totalBytes;
    private long _totalErrors;

    public long TotalConnections => Interlocked.Read(ref _totalConnections);
    public long ActiveConnections => Interlocked.Read(ref _activeConnections);
    public long TotalRequests => Interlocked.Read(ref _totalRequests);
    public long TotalBytes => Interlocked.Read(ref _totalBytes);
    public long TotalErrors => Interlocked.Read(ref _totalErrors);

    public void ConnectionOpened()
    {
        Interlocked.Increment(ref _totalConnections);
        Interlocked.Increment(ref _activeConnections);
    }

    public void ConnectionClosed()
    {
        Interlocked.Decrement(ref _activeConnections);
    }

    public void RequestCompleted(long bytes)
    {
        Interlocked.Increment(ref _totalRequests);
        Interlocked.Add(ref _totalBytes, bytes);
    }

    public void ErrorOccurred()
    {
        Interlocked.Increment(ref _totalErrors);
    }

    public void Reset()
    {
        Interlocked.Exchange(ref _totalConnections, 0);
        Interlocked.Exchange(ref _activeConnections, 0);
        Interlocked.Exchange(ref _totalRequests, 0);
        Interlocked.Exchange(ref _totalBytes, 0);
        Interlocked.Exchange(ref _totalErrors, 0);
    }
}
