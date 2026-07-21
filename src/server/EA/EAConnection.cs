using System.Buffers;
using System.Text;
using System.Threading.Channels;
using Arcadia.EA.Constants;
using Microsoft.Extensions.Logging;
using Org.BouncyCastle.Tls;

namespace Arcadia.EA;

public interface IEAConnection : IAsyncDisposable
{
    string RemoteEndpoint { get; }
    Stream? NetworkStream { get; }
    Stream? Transport { get; set; }
    string RemoteAddress { get; }
    string LocalAddress { get; }

    void Initialize(Stream network, string remoteEndpoint, string localEndpoint, CancellationToken ct);
    Task Terminate();

    IAsyncEnumerable<Packet> ReceiveAsync(ILogger logger);
    Task<bool> SendPacket(Packet packet);
}

public sealed class EAConnection : IEAConnection
{
    public string RemoteEndpoint { get; private set; } = string.Empty;
    public string RemoteAddress => RemoteEndpoint.Split(':')[0];
    public string LocalAddress => _serverAddress;
    public Stream? NetworkStream { get; private set; }
    public Stream? Transport { get; set; }

    private const int ReadBufferSize = 8192;

    private ILogger? _logger;
    private string _serverAddress = null!;
    private CancellationTokenSource _cts = null!;
    private int _pumpStarted;

    // Reads are pumped by a dedicated thread: the TLS stream has no true async support, so a
    // pool-based ReadAsync parks a pool thread per connection and serializes against writes.
    private readonly Channel<Packet> _rxChannel = Channel.CreateUnbounded<Packet>(new UnboundedChannelOptions
    {
        SingleReader = true,
        SingleWriter = true,
        AllowSynchronousContinuations = false,
    });

    // SendPacket can be called from multiple threads (messenger presence pushes); serialize writes.
    private readonly SemaphoreSlim _sendLock = new(1, 1);

    public void Initialize(Stream network, string remoteEndpoint, string localEndpoint, CancellationToken ct)
    {
        if (NetworkStream is not null)
        {
            throw new InvalidOperationException("Tried to initialize an already initialized connection!");
        }

        RemoteEndpoint = remoteEndpoint;
        NetworkStream = network;
        Transport ??= network;

        _serverAddress = localEndpoint.Split(':')[0];
        _cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
    }

    public async IAsyncEnumerable<Packet> ReceiveAsync(ILogger? parentLogger)
    {
        _logger = parentLogger;
        if (NetworkStream is null) throw new InvalidOperationException("Connection must be initialized before starting");

        if (Interlocked.Exchange(ref _pumpStarted, 1) == 0)
        {
            var pump = new Thread(ReadPump) { IsBackground = true, Name = $"ea-rx {RemoteEndpoint}" };
            pump.Start();
        }

        await foreach (var packet in _rxChannel.Reader.ReadAllAsync())
        {
            yield return packet;
        }

        _logger?.LogTrace("Connection has been closed: {endpoint}", RemoteEndpoint);
    }

    private void ReadPump()
    {
        byte[] readBuffer = ArrayPool<byte>.Shared.Rent(ReadBufferSize);
        using var multiPacketBuffer = new MemoryStream(ReadBufferSize);

        uint? currentMultiPacketId = null;
        uint requestedMultiPacketSize = 0;
        long bufferedMultiPacketSize = 0;

        // Must persist across iterations: a single TCP read may hold only a partial packet.
        var buffered = 0;
        var keepGoing = true;

        try
        {
            while (keepGoing && NetworkStream!.CanRead && !_cts.IsCancellationRequested)
            {
                while (buffered < Packet.HEADER_SIZE)
                {
                    int read = ReadOrZero(readBuffer, buffered, "header");
                    if (read <= 0)
                    {
                        keepGoing = false;
                        break;
                    }
                    buffered += read;
                }
                if (!keepGoing) break;

                uint packetLength = ((uint)readBuffer[8] << 24)
                                  | ((uint)readBuffer[9] << 16)
                                  | ((uint)readBuffer[10] << 8)
                                  |  (uint)readBuffer[11];

                if (packetLength < Packet.HEADER_SIZE || packetLength > ReadBufferSize)
                {
                    _logger?.LogWarning(
                        "Malformed packet from {ep}: declared length {len} (header={h}, max={max}); closing connection",
                        RemoteEndpoint, packetLength, Packet.HEADER_SIZE, ReadBufferSize);
                    break;
                }

                while (buffered < (int)packetLength)
                {
                    int read = ReadOrZero(readBuffer, buffered, "body");
                    if (read <= 0)
                    {
                        keepGoing = false;
                        break;
                    }
                    buffered += read;
                }
                if (!keepGoing) break;

                Packet packet;
                try
                {
                    packet = new Packet(readBuffer.AsSpan(0, (int)packetLength).ToArray());
                }
                catch (Exception e)
                {
                    _logger?.LogWarning(
                        e, "Failed to parse packet from {ep} (len={len}); closing connection",
                        RemoteEndpoint, packetLength);
                    break;
                }

                var remaining = buffered - (int)packetLength;
                if (remaining > 0)
                {
                    Buffer.BlockCopy(readBuffer, (int)packetLength, readBuffer, 0, remaining);
                }
                buffered = remaining;

                if (packet.TransmissionType == FeslTransmissionType.MultiPacketResponse || packet.TransmissionType == FeslTransmissionType.MultiPacketRequest)
                {
                    var encodedPart = packet["data"].Replace("%3d", "=");

                    var partPayload = Convert.FromBase64String(encodedPart);
                    var size = uint.Parse(packet["size"]);

                    _logger?.LogTrace("Multi-packet part received - ID: {id}, Declared Size: {size}, Part Size: {partSize}", packet.Id, size, encodedPart.Length);

                    if (currentMultiPacketId != packet.Id)
                    {
                        currentMultiPacketId = packet.Id;
                        requestedMultiPacketSize = size;

                        multiPacketBuffer.SetLength(0);
                        multiPacketBuffer.Position = 0;
                        bufferedMultiPacketSize = 0;
                    }

                    if (requestedMultiPacketSize != size) throw new Exception($"Requested packet-size changed between requests! Initial size: {requestedMultiPacketSize}, newSize: {size}");

                    multiPacketBuffer.Write(partPayload, 0, partPayload.Length);
                    bufferedMultiPacketSize += encodedPart.Length;

                    _logger?.LogTrace("Multi-packet part buffered - Length: {bufferLength}, Requested Size: {requestedSize}",
                        bufferedMultiPacketSize, requestedMultiPacketSize);

                    if (bufferedMultiPacketSize == requestedMultiPacketSize)
                    {
                        currentMultiPacketId = null;

                        var bufferData = multiPacketBuffer.ToArray();
                        var combinedData = Utils.ParseFeslPacketToDict(bufferData);
                        var combinedPacket = new Packet(packet.Type, packet.TransmissionType, packet.Id, combinedData, size);

                        _logger?.LogTrace("'{type}' incoming multi-packet, combined:{data}", combinedPacket.Type, Encoding.ASCII.GetString(bufferData));
                        if (!_rxChannel.Writer.TryWrite(combinedPacket)) break;
                    }
                    else if (bufferedMultiPacketSize > requestedMultiPacketSize) throw new Exception($"Buffer overflow! Buffer contains {multiPacketBuffer.Length} bytes but expected only {requestedMultiPacketSize}");

                    continue;
                }
                else
                {
                    currentMultiPacketId = null;
                    _logger?.LogTrace("'{type}' incoming:{data}", packet.Type, Encoding.ASCII.GetString(packet.Data ?? []));
                }

                if (!_rxChannel.Writer.TryWrite(packet)) break;
            }
        }
        catch (Exception e)
        {
            _logger?.LogWarning(e, "Receive pump failed for {endpoint}; closing connection", RemoteEndpoint);
        }
        finally
        {
            _rxChannel.Writer.TryComplete();
            ArrayPool<byte>.Shared.Return(readBuffer, clearArray: true);
        }
    }

    private int ReadOrZero(byte[] buffer, int offset, string stage)
    {
        try
        {
            return NetworkStream!.Read(buffer, offset, ReadBufferSize - offset);
        }
        catch (ObjectDisposedException) { return 0; }
        catch (TlsNoCloseNotifyException) { return 0; }
        catch (EndOfStreamException) { return 0; }
        catch (Exception e)
        {
            _logger?.LogDebug(e, "Failed to read client stream ({stage}), endpoint: {endpoint}", stage, RemoteEndpoint);
            return 0;
        }
    }

    public Task Terminate()
    {
        _cts.Cancel();
        // Disposing the raw transport unblocks a pump parked in Read immediately; disposing
        // the TLS stream instead would try to write close_notify to a possibly-dead client.
        try { Transport?.Dispose(); } catch { }
        return Task.CompletedTask;
    }

    public async Task<bool> SendPacket(Packet packet)
    {
        if (NetworkStream is null || !NetworkStream.CanWrite)
        {
            return false;
        }

        var packetBuffer = await packet.Serialize();
        return await SendBinary(packetBuffer);
    }

    private async Task<bool> SendBinary(byte[] buffer)
    {
        if (NetworkStream is null || !NetworkStream.CanWrite)
        {
            _logger?.LogDebug("Tried writing to disconnected endpoint: {endpoint}!", RemoteEndpoint);
            return false;
        }

        var acquired = false;
        try
        {
            await _sendLock.WaitAsync();
            acquired = true;
            // Sync on purpose: WriteAsync on this stream deadlock-queues behind the pump's blocking Read.
            NetworkStream.Write(buffer);
            NetworkStream.Flush();
            _logger?.LogTrace("data sent:{data}", Encoding.ASCII.GetString(buffer));
            return true;
        }
        catch (Exception e)
        {
            _logger?.LogDebug(e, "Failed writing to endpoint: {endpoint}!", RemoteEndpoint);
            _cts.Cancel();
            return false;
        }
        finally
        {
            if (acquired) _sendLock.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        _rxChannel.Writer.TryComplete();
        _cts?.Dispose();
        await ValueTask.CompletedTask;
    }
}
