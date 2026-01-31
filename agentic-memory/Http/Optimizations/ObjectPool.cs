using System.Buffers;

namespace AgenticMemory.Http.Optimizations;

/// <summary>
/// Generic high-performance object pool for reducing allocations
/// </summary>
/// <typeparam name="T">Type to pool</typeparam>
public sealed class ObjectPool<T> where T : class
{
    private readonly Func<T> _factory;
    private readonly Action<T>? _reset;
    private readonly T?[] _items;
    private T? _fastItem;

    public ObjectPool(Func<T> factory, Action<T>? reset = null, int maxSize = 16)
    {
        _factory = factory ?? throw new ArgumentNullException(nameof(factory));
        _reset = reset;
        _items = new T?[maxSize];
    }

    public T Rent()
    {
        // Try fast path first
        var item = _fastItem;
        if (item is not null && Interlocked.CompareExchange(ref _fastItem, null, item) == item)
        {
            return item;
        }

        // Search the pool
        var items = _items;
        for (int i = 0; i < items.Length; i++)
        {
            item = items[i];
            if (item is not null && Interlocked.CompareExchange(ref items[i], null, item) == item)
            {
                return item;
            }
        }

        // Create new instance
        return _factory();
    }

    public void Return(T item)
    {
        _reset?.Invoke(item);

        // Try fast path first
        if (_fastItem is null && Interlocked.CompareExchange(ref _fastItem, item, null) is null)
        {
            return;
        }

        // Find empty slot
        var items = _items;
        for (int i = 0; i < items.Length; i++)
        {
            if (Interlocked.CompareExchange(ref items[i], item, null) is null)
            {
                return;
            }
        }

        // Pool is full, item will be GC'd
    }
}

/// <summary>
/// Pool for Dictionary instances to avoid allocations
/// </summary>
public static class DictionaryPool
{
    private static readonly ObjectPool<Dictionary<string, string>> Pool = new(
        factory: () => new Dictionary<string, string>(16, StringComparer.OrdinalIgnoreCase),
        reset: dict => dict.Clear(),
        maxSize: 64
    );

    public static Dictionary<string, string> Rent() => Pool.Rent();
    public static void Return(Dictionary<string, string> dict) => Pool.Return(dict);
}

/// <summary>
/// Pool for StringBuilder instances
/// </summary>
public static class StringBuilderPool
{
    private static readonly ObjectPool<System.Text.StringBuilder> Pool = new(
        factory: () => new System.Text.StringBuilder(256),
        reset: sb => sb.Clear(),
        maxSize: 32
    );

    public static System.Text.StringBuilder Rent() => Pool.Rent();
    public static void Return(System.Text.StringBuilder sb) => Pool.Return(sb);
}

/// <summary>
/// Pool for MemoryStream instances used in response writing
/// </summary>
public static class MemoryStreamPool
{
    private static readonly ObjectPool<RecyclableMemoryStream> Pool = new(
        factory: () => new RecyclableMemoryStream(4096),
        reset: ms => ms.Reset(),
        maxSize: 64
    );

    public static RecyclableMemoryStream Rent() => Pool.Rent();
    public static void Return(RecyclableMemoryStream stream) => Pool.Return(stream);
}

/// <summary>
/// Recyclable MemoryStream that can be reset without deallocation
/// </summary>
public sealed class RecyclableMemoryStream : Stream
{
    private byte[] _buffer;
    private int _position;
    private int _length;

    public RecyclableMemoryStream(int initialCapacity)
    {
        _buffer = ArrayPool<byte>.Shared.Rent(initialCapacity);
        _position = 0;
        _length = 0;
    }

    public override bool CanRead => true;
    public override bool CanSeek => true;
    public override bool CanWrite => true;
    public override long Length => _length;
    public override long Position
    {
        get => _position;
        set => _position = (int)value;
    }

    public void Reset()
    {
        _position = 0;
        _length = 0;
    }

    public override void Flush() { }

    public override int Read(byte[] buffer, int offset, int count)
    {
        int available = _length - _position;
        if (available <= 0) return 0;

        int toCopy = Math.Min(available, count);
        Array.Copy(_buffer, _position, buffer, offset, toCopy);
        _position += toCopy;
        return toCopy;
    }

    public override long Seek(long offset, SeekOrigin origin)
    {
        _position = origin switch
        {
            SeekOrigin.Begin => (int)offset,
            SeekOrigin.Current => _position + (int)offset,
            SeekOrigin.End => _length + (int)offset,
            _ => throw new ArgumentException("Invalid origin", nameof(origin))
        };
        return _position;
    }

    public override void SetLength(long value)
    {
        EnsureCapacity((int)value);
        _length = (int)value;
    }

    public override void Write(byte[] buffer, int offset, int count)
    {
        EnsureCapacity(_position + count);
        Array.Copy(buffer, offset, _buffer, _position, count);
        _position += count;
        if (_position > _length)
            _length = _position;
    }

    public override void Write(ReadOnlySpan<byte> buffer)
    {
        EnsureCapacity(_position + buffer.Length);
        buffer.CopyTo(_buffer.AsSpan(_position));
        _position += buffer.Length;
        if (_position > _length)
            _length = _position;
    }

    public ReadOnlySpan<byte> GetBuffer() => _buffer.AsSpan(0, _length);

    public ReadOnlyMemory<byte> GetMemory() => _buffer.AsMemory(0, _length);

    private void EnsureCapacity(int required)
    {
        if (required <= _buffer.Length)
            return;

        int newSize = Math.Max(required, _buffer.Length * 2);
        var newBuffer = ArrayPool<byte>.Shared.Rent(newSize);
        Array.Copy(_buffer, newBuffer, _length);
        ArrayPool<byte>.Shared.Return(_buffer);
        _buffer = newBuffer;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing && _buffer is not null)
        {
            ArrayPool<byte>.Shared.Return(_buffer);
            _buffer = null!;
        }
        base.Dispose(disposing);
    }
}
