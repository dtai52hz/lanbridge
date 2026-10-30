using System.Runtime.CompilerServices;
using System.Threading.Channels;

namespace LanBridge;

internal sealed record SessionPacket(Packet Packet, Permissions? Permissions = null,
    long FilesEpoch = 0, long ClipboardEpoch = 0, long AudioEpoch = 0, long FeaturesEpoch = 0);

internal readonly record struct SessionInboxSnapshot(int Packets, long Bytes);

/// <summary>
/// One ordered, bounded session queue. Only adjacent absolute pointer positions may
/// replace one another; a key, text edit, button, wheel or session boundary is never
/// overtaken. The network reader stays independent of the UI and the input pump.
/// </summary>
internal sealed class SessionInbox
{
    readonly object _gate = new();
    readonly LinkedList<SessionPacket> _packets = new();
    readonly Channel<bool> _ready = Channel.CreateBounded<bool>(new BoundedChannelOptions(1)
    {
        SingleReader = true, SingleWriter = false,
        FullMode = BoundedChannelFullMode.DropWrite,
        AllowSynchronousContinuations = false
    });
    readonly int _capacity;
    readonly int _byteCapacity;
    long _bytes;
    bool _completed;

    public SessionInbox(int capacity = 512) : this(capacity, int.MaxValue) { }
    public SessionInbox(int capacity, int byteCapacity)
    {
        _capacity = capacity > 0 ? capacity : throw new ArgumentOutOfRangeException(nameof(capacity));
        _byteCapacity = byteCapacity > 0 ? byteCapacity : throw new ArgumentOutOfRangeException(nameof(byteCapacity));
    }

    public SessionInboxSnapshot GetSnapshot() { lock (_gate) return new(_packets.Count, _bytes); }

    public bool TryWrite(Packet packet) => TryWriteSession(new(packet));

    public bool TryWriteSession(SessionPacket item)
    {
        var packet = item.Packet;
        lock (_gate)
        {
            if (_completed) return false;
            if (packet.Type == "move" && _packets.Last is { Value.Packet.Type: "move" } last)
            {
                // Coalescing keeps the latest position while carrying an outstanding
                // diagnostic receipt token. Losing a token is not lost physical input,
                // but retaining it lets a congested path still produce a useful sample.
                if (packet.Id is null && packet.Length == 0 && last.Value.Packet is { Id.Length: 32, Length: > 0 } previous
                    && Guid.TryParseExact(previous.Id, "N", out _))
                {
                    packet = packet with { Id = previous.Id, Length = previous.Length };
                    item = item with { Packet = packet };
                }
                long oldSize = Size(last.Value.Packet), newSize = Size(packet);
                if (newSize - oldSize > _byteCapacity - _bytes) return true;
                _bytes -= oldSize;
                last.Value = item;
                _bytes += newSize;
                return true;
            }
            long size = Size(packet);
            if (_packets.Count >= _capacity || size > _byteCapacity - _bytes)
                return packet.Type is "move" or "audio";
            _packets.AddLast(item);
            _bytes += size;
            _ready.Writer.TryWrite(true);
            return true;
        }
    }

    public async IAsyncEnumerable<Packet> ReadAllAsync([EnumeratorCancellation] CancellationToken cancellationToken)
    {
        while (await _ready.Reader.WaitToReadAsync(cancellationToken).ConfigureAwait(false))
        {
            while (_ready.Reader.TryRead(out _)) { }
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                Packet packet;
                lock (_gate)
                {
                    if (_packets.First is not { } first) break;
                    packet = first.Value.Packet;
                    _bytes -= Size(packet);
                    _packets.RemoveFirst();
                }
                yield return packet;
            }
        }
    }

    /// <summary>Amortize the input-pump dispatch while retaining packet order and protocol barriers.</summary>
    public async IAsyncEnumerable<List<SessionPacket>> ReadBatchesAsync([EnumeratorCancellation] CancellationToken cancellationToken)
    {
        while (await _ready.Reader.WaitToReadAsync(cancellationToken).ConfigureAwait(false))
        {
            while (_ready.Reader.TryRead(out _)) { }
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var batch = new List<SessionPacket>(32);
                lock (_gate)
                {
                    while (batch.Count < 32 && _packets.First is { } first)
                    {
                        batch.Add(first.Value);
                        _bytes -= Size(first.Value.Packet);
                        _packets.RemoveFirst();
                    }
                }
                if (batch.Count == 0) break;
                yield return batch;
            }
        }
    }

    public void Complete()
    {
        lock (_gate)
        {
            _completed = true;
            _packets.Clear();
            _bytes = 0;
            _ready.Writer.TryComplete();
        }
    }

    static long Size(Packet packet) => 256L + (packet.Data?.Length ?? 0) + (packet.Text?.Length ?? 0) * 2L
        + (packet.ClipboardEntries?.Sum(entry => 128L + (entry?.Path?.Length ?? 0) * 2L) ?? 0);
}
