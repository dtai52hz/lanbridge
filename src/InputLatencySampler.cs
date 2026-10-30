using System.Diagnostics;

namespace LanBridge;

/// <summary>Correlates occasional input-pump receipts using this PC's monotonic clock only.</summary>
internal sealed class InputLatencySampler
{
    readonly object _gate = new();
    readonly Dictionary<string, long> _pending = new(StringComparer.Ordinal);
    ConnectionLatency? _latency;
    long _lastSample;
    bool _stopped;
    internal ConnectionLatency? Latency { get { lock (_gate) return _latency; } }

    internal Packet Stamp(Packet packet)
    {
        if (packet.Type is not ("move" or "button" or "wheel" or "key" or "text") || packet.Id != null || packet.Length != 0) return packet;
        long now = Stopwatch.GetTimestamp();
        lock (_gate)
        {
            if (_stopped || (_lastSample != 0 && Stopwatch.GetElapsedTime(_lastSample, now).TotalMilliseconds < 500)) return packet;
            foreach (var id in _pending.Where(pair => Stopwatch.GetElapsedTime(pair.Value, now).TotalSeconds >= 5).Select(pair => pair.Key).ToArray()) _pending.Remove(id);
            if (_pending.Count >= 16) return packet;
            string nonce = Guid.NewGuid().ToString("N");
            _lastSample = now;
            _pending.Add(nonce, now);
            return packet with { Id = nonce, Length = now };
        }
    }

    internal bool Accept(Packet receipt)
    {
        if (receipt.Type != "input-ack" || receipt.Id is not { Length: 32 } || receipt.Length <= 0) return false;
        lock (_gate)
        {
            if (_stopped || !_pending.TryGetValue(receipt.Id, out long sent) || receipt.Length != sent) return false;
            _pending.Remove(receipt.Id);
            double elapsed = Stopwatch.GetElapsedTime(sent).TotalMilliseconds;
            if (elapsed is < 0 or >= 5000) return false;
            var previous = _latency;
            _latency = new(elapsed, previous == null ? elapsed : previous.SmoothedMilliseconds + (elapsed - previous.SmoothedMilliseconds) / 8,
                previous == null ? elapsed : Math.Min(previous.MinimumMilliseconds, elapsed), previous == null ? elapsed : Math.Max(previous.MaximumMilliseconds, elapsed),
                (previous?.Samples ?? 0) + 1, DateTimeOffset.UtcNow);
            return true;
        }
    }

    internal void Stop() { lock (_gate) { _stopped = true; _pending.Clear(); } }
}
