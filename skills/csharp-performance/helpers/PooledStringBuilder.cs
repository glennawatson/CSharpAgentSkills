// Drop-in helper — change the namespace to suit your project.
using System.Collections.Concurrent;
using System.Text;

namespace PerfHelpers;

/// <summary>
/// A small pool of reusable <see cref="StringBuilder"/> instances. Renting one avoids
/// allocating and garbage-collecting a builder on every formatting hot path.
/// </summary>
/// <remarks>
/// Builders larger than <see cref="MaxRetainedCapacity"/> are dropped on return so a single
/// oversized build does not keep a large buffer alive. Do not let the rented builder's
/// reference escape the rental scope.
/// </remarks>
public static class PooledStringBuilder
{
    private const int DefaultCapacity = 256;
    private const int MaxRetainedCapacity = 64 * 1024;
    private const int MaxPooled = 16;

    private static readonly ConcurrentQueue<StringBuilder> Pool = new();
    private static int _count;

    /// <summary>Rents a cleared builder. Dispose the returned rental to give it back.</summary>
    public static Rental Rent()
    {
        if (Pool.TryDequeue(out var sb))
        {
            Interlocked.Decrement(ref _count);
            return new Rental(sb);
        }

        return new Rental(new StringBuilder(DefaultCapacity));
    }

    private static void Return(StringBuilder sb)
    {
        if (sb.Capacity > MaxRetainedCapacity)
        {
            return;
        }

        if (Interlocked.Increment(ref _count) > MaxPooled)
        {
            Interlocked.Decrement(ref _count);
            return;
        }

        sb.Clear();
        Pool.Enqueue(sb);
    }

    /// <summary>A scoped rental that returns its builder to the pool on <see cref="Dispose"/>.</summary>
    public readonly struct Rental : IDisposable
    {
        internal Rental(StringBuilder builder) => Builder = builder;

        /// <summary>The rented builder. Do not use it after disposing the rental.</summary>
        public StringBuilder Builder { get; }

        /// <summary>Returns the builder's current contents as a string.</summary>
        public override string ToString() => Builder.ToString();

        /// <summary>Returns the builder to the pool.</summary>
        public void Dispose() => Return(Builder);
    }
}
