namespace AgenticMemory.Http.Configuration;

/// <summary>
/// Server configuration options
/// </summary>
public class ServerOptions
{
    /// <summary>
    /// TCP port to listen on
    /// </summary>
    public int Port { get; set; } = 3377;

    /// <summary>
    /// IP address to bind (0.0.0.0 = all interfaces)
    /// </summary>
    public string BindAddress { get; set; } = "0.0.0.0";

    /// <summary>
    /// Maximum simultaneous connections
    /// </summary>
    public int MaxConcurrentConnections { get; set; } = 1000;

    /// <summary>
    /// Connection timeout
    /// </summary>
    public TimeSpan ConnectionTimeout { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Single request timeout
    /// </summary>
    public TimeSpan RequestTimeout { get; set; } = TimeSpan.FromSeconds(10);

    /// <summary>
    /// Graceful shutdown timeout
    /// </summary>
    public TimeSpan ShutdownTimeout { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Maximum request size (10MB default)
    /// </summary>
    public int MaxRequestSize { get; set; } = 10485760;

    /// <summary>
    /// Maximum header size in bytes
    /// </summary>
    public int MaxHeaderSize { get; set; } = 8192;

    /// <summary>
    /// Support HTTP keep-alive
    /// </summary>
    public bool EnableKeepAlive { get; set; } = false;

    /// <summary>
    /// Server header value
    /// </summary>
    public string ServerName { get; set; } = "AgenticMemory/1.0";
}
