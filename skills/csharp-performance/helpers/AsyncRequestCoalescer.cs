// Drop-in helper — change the namespace to suit your project.
using System.Collections.Concurrent;

namespace PerfHelpers;

/// <summary>
/// Coalesces concurrent requests for the same key into a single in-flight operation. When many
/// callers ask for the same key at once, the factory runs once and every caller awaits the same
/// result, eliminating duplicate network or disk work (the "thundering herd").
/// </summary>
/// <typeparam name="TKey">Request key; must have meaningful equality.</typeparam>
/// <typeparam name="TValue">Result type.</typeparam>
/// <remarks>
/// This coalesces in-flight work only; it is not a result cache. Once an operation completes or
/// faults its entry is removed, so the next call re-runs the factory. Wrap it with your own cache
/// if you want to retain results.
/// </remarks>
public sealed class AsyncRequestCoalescer<TKey, TValue>
    where TKey : notnull
{
    private readonly ConcurrentDictionary<TKey, Lazy<Task<TValue>>> _inflight = new();

    /// <summary>
    /// Returns the in-flight task for <paramref name="key"/>, starting <paramref name="factory"/>
    /// only when none is running. Concurrent callers for the same key share one task.
    /// </summary>
    public Task<TValue> GetAsync(TKey key, Func<TKey, Task<TValue>> factory)
    {
        // Lazy guarantees the factory runs at most once even if GetOrAdd races to create two.
        var lazy = _inflight.GetOrAdd(
            key,
            static (k, state) => new Lazy<Task<TValue>>(() => Run(k, state)),
            (Coalescer: this, Factory: factory));

        return lazy.Value;
    }

    private static async Task<TValue> Run(
        TKey key,
        (AsyncRequestCoalescer<TKey, TValue> Coalescer, Func<TKey, Task<TValue>> Factory) state)
    {
        try
        {
            return await state.Factory(key).ConfigureAwait(false);
        }
        finally
        {
            state.Coalescer._inflight.TryRemove(key, out _);
        }
    }
}
