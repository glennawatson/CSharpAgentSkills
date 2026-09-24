// Drop-in helper — change the namespace to suit your project.
namespace PerfHelpers;

/// <summary>
/// A list tuned for many concurrent reads and rare writes. Readers see an immutable snapshot
/// with no locking; writers clone and publish a new array under a lock. Trade write cost (an
/// array copy) for lock-free, allocation-free reads — ideal for registries, handler lists, and
/// converter tables read far more often than they change.
/// </summary>
/// <typeparam name="T">Element type.</typeparam>
public sealed class CopyOnWriteList<T>
{
    private readonly object _writeGate = new();
    private T[] _items = [];

    /// <summary>
    /// Gets the current snapshot. Safe to enumerate without locking; treat it as immutable and
    /// never mutate the returned array.
    /// </summary>
    public T[] Snapshot => Volatile.Read(ref _items);

    /// <summary>Adds an item, publishing a new snapshot atomically.</summary>
    public void Add(T item)
    {
        lock (_writeGate)
        {
            var current = _items;
            var next = new T[current.Length + 1];
            Array.Copy(current, next, current.Length);
            next[current.Length] = item;
            Volatile.Write(ref _items, next);
        }
    }

    /// <summary>Removes the first matching item if present, publishing a new snapshot.</summary>
    /// <returns><see langword="true"/> if an item was removed; otherwise <see langword="false"/>.</returns>
    public bool Remove(T item)
    {
        lock (_writeGate)
        {
            var current = _items;
            var index = Array.IndexOf(current, item);
            if (index < 0)
            {
                return false;
            }

            if (current.Length == 1)
            {
                Volatile.Write(ref _items, []);
                return true;
            }

            var next = new T[current.Length - 1];
            Array.Copy(current, next, index);
            Array.Copy(current, index + 1, next, index, current.Length - index - 1);
            Volatile.Write(ref _items, next);
            return true;
        }
    }
}
