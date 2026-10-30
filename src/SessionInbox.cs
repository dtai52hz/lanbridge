using System.Runtime.CompilerServices;
using System.Threading.Channels;

namespace LanBridge;

/// <summary>
/// One ordered, bounded session queue. Only adjacent absolute pointer positions may
/// replace one another; a key, text edit, button, wheel or session boundary is never
/// overtaken. The network reader stays independent of the UI and the input pump.
/// </summary>
internal sealed class SessionInbox
{
    readonly object _gate = new();
    readonly LinkedList<Packet> _packets = new();
    readonly Channel<bool> _ready = Channel.CreateBounded<bool>(new BoundedChannelOptions(1)
    {
        SingleReader = true, SingleWriter = false,
        FullMode = BoundedChannelFullMode.DropWrite,
        AllowSynchronousContinuations = false
    });
    readonly int _capacity;
    bool _completed;

    public SessionInbox(int capacity = 512) => _capacity = capacity > 0 ? capacity : throw new ArgumentOutOfRangeException(nameof(capacity));

    public bool TryWrite(Packet packet)
    {
        lock (_gate)
        {
            if (_completed) return false;
            if (packet.Type == "move" && _packets.Last is { Value.Type: "move" } last)
            {
                last.Value = packet;
                return true;
            }
            if (_packets.Count >= _capacity)
                return packet.Type is "move" or "audio";
            _packets.AddLast(packet);
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
                    packet = first.Value;
                    _packets.RemoveFirst();
                }
                yield return packet;
            }
        }
    }

    /// <summary>Amortize the input-pump dispatch while retaining packet order and protocol barriers.</summary>
    public async IAsyncEnumerable<List<Packet>> ReadBatchesAsync([EnumeratorCancellation] CancellationToken cancellationToken)
    {
        while (await _ready.Reader.WaitToReadAsync(cancellationToken).ConfigureAwait(false))
        {
            while (_ready.Reader.TryRead(out _)) { }
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var batch = new List<Packet>(32);
                lock (_gate)
                {
                    while (batch.Count < 32 && _packets.First is { } first)
                    {
                        batch.Add(first.Value);
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
            _ready.Writer.TryComplete();
        }
    }
}
