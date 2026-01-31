using System.Net;
using System.Net.Sockets;

namespace AgenticMemory.Http.Models;

/// <summary>
/// Represents the context of a TCP connection
/// </summary>
public class ConnectionContext : IDisposable
{
    /// <summary>
    /// Unique connection identifier
    /// </summary>
    public Guid ConnectionId { get; }

    /// <summary>
    /// TCP client instance
    /// </summary>
    public TcpClient Client { get; }

    /// <summary>
    /// Network stream for reading/writing
    /// </summary>
    public NetworkStream Stream { get; }

    /// <summary>
    /// Client IP and port
    /// </summary>
    public IPEndPoint? RemoteEndPoint { get; }

    /// <summary>
    /// Connection timestamp
    /// </summary>
    public DateTime ConnectedAt { get; }

    /// <summary>
    /// Keep connection alive after request
    /// </summary>
    public bool KeepAlive { get; set; }

    public ConnectionContext(TcpClient client)
    {
        ConnectionId = Guid.NewGuid();
        Client = client;
        Stream = client.GetStream();
        RemoteEndPoint = client.Client.RemoteEndPoint as IPEndPoint;
        ConnectedAt = DateTime.UtcNow;
        KeepAlive = false;
    }

    public void Dispose()
    {
        Stream.Dispose();
        Client.Dispose();
    }
}
