// Drop-in helper — change the namespace to suit your project.
namespace PerfHelpers;

/// <summary>
/// Lock-free helpers for assign-once / dispose-once disposable fields, built on
/// <see cref="Interlocked"/> instead of locks.
/// </summary>
public static class AtomicDisposable
{
    /// <summary>
    /// Atomically assigns <paramref name="value"/> to <paramref name="field"/> when it is still
    /// null. If the field was already set, <paramref name="value"/> is disposed instead and this
    /// returns <see langword="false"/>, making "set exactly once" safe under races.
    /// </summary>
    public static bool TrySet(ref IDisposable? field, IDisposable value)
    {
        if (Interlocked.CompareExchange(ref field, value, null) is null)
        {
            return true;
        }

        value.Dispose();
        return false;
    }

    /// <summary>
    /// Atomically swaps <paramref name="field"/> to null and disposes whatever was there. Safe to
    /// call concurrently and repeatedly; only the winning caller disposes.
    /// </summary>
    public static void Dispose(ref IDisposable? field)
        => Interlocked.Exchange(ref field, null)?.Dispose();
}
