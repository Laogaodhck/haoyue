using System.Collections.Concurrent;
using Haoyue.Runtime.Configuration;

namespace Haoyue.Runtime.Providers;

/// <summary>Per-model circuit breaker: opens after N consecutive failures, half-opens after a cooldown.</summary>
/// <remarks>
/// Thresholds and cooldowns are resolved live on every call (via the injected factory or the
/// snapshot), so a config reload changes breaker behavior immediately. Half-open admits a
/// single probe per cooldown window (Interlocked flag) instead of letting every concurrent
/// turn hammer a failing model, and 429 rate-limit / 5xx server failures cool down longer
/// than generic errors.
/// </remarks>
public sealed class CircuitBreaker
{
    private sealed class Circuit
    {
        public int ConsecutiveFailures;
        public DateTimeOffset OpenedAt;
        /// <summary>Cooldown override chosen by the last failure's error category (0 = use config).</summary>
        public double CooldownSeconds;
        /// <summary>Single-flight probe gate for the half-open state (1 = a probe is out).</summary>
        public int Probing;
    }

    private readonly ConcurrentDictionary<string, Circuit> _circuits = new(StringComparer.OrdinalIgnoreCase);
    private readonly Func<RetryConfig> _config;

    public CircuitBreaker(RetryConfig config) : this(() => config) { }

    /// <summary>Resolves thresholds live so a config reload is honored without rebuilding the breaker.</summary>
    public CircuitBreaker(Func<RetryConfig> configFactory) => _config = configFactory;

    private RetryConfig Config => _config();

    /// <summary>True while the model is cooling down. Read-only: does not consume a probe slot.</summary>
    public bool IsOpen(string modelRef)
    {
        if (!_circuits.TryGetValue(modelRef, out var circuit)) return false;
        lock (circuit)
        {
            if (circuit.ConsecutiveFailures < Math.Max(1, Config.CircuitBreakThreshold)) return false;
            return DateTimeOffset.UtcNow - circuit.OpenedAt < CooldownFor(circuit);
        }
    }

    /// <summary>
    /// Gate before an attempt: passes when the circuit is closed, or when it is half-open
    /// and this caller wins the single-probe race. Losers return false so the failover
    /// loop skips the model instead of thundering-herd probing it.
    /// </summary>
    public bool TryBeginProbe(string modelRef)
    {
        if (!_circuits.TryGetValue(modelRef, out var circuit)) return true;
        lock (circuit)
        {
            if (circuit.ConsecutiveFailures < Math.Max(1, Config.CircuitBreakThreshold)) return true;
            if (DateTimeOffset.UtcNow - circuit.OpenedAt < CooldownFor(circuit)) return false;
            return Interlocked.CompareExchange(ref circuit.Probing, 1, 0) == 0;
        }
    }

    public void RecordSuccess(string modelRef) => _circuits.TryRemove(modelRef, out _);

    public void RecordFailure(string modelRef, int? statusCode = null)
    {
        var circuit = _circuits.GetOrAdd(modelRef, _ => new Circuit());
        lock (circuit)
        {
            circuit.ConsecutiveFailures++;
            circuit.Probing = 0; // a failed probe ends the window; the next cooldown re-opens it
            if (circuit.ConsecutiveFailures >= Math.Max(1, Config.CircuitBreakThreshold))
            {
                circuit.OpenedAt = DateTimeOffset.UtcNow;
                circuit.CooldownSeconds = CooldownFor(statusCode);
            }
        }
    }

    public int FailureCount(string modelRef) =>
        _circuits.TryGetValue(modelRef, out var circuit) ? circuit.ConsecutiveFailures : 0;

    private TimeSpan CooldownFor(Circuit circuit)
    {
        var seconds = circuit.CooldownSeconds > 0
            ? circuit.CooldownSeconds
            : Config.CircuitCooldownSeconds;
        return TimeSpan.FromSeconds(Math.Max(0.1, seconds));
    }

    /// <summary>429 rate limits and 5xx outages deserve longer cooldowns than generic errors.</summary>
    private static double CooldownFor(int? statusCode) => statusCode switch
    {
        429 => 300.0,
        >= 500 => 120.0,
        _ => 0.0 // fall back to the configured cooldown
    };
}
