// Drop-in helper — change the namespace to suit your project.
namespace PerfHelpers;

/// <summary>
/// Caches <c>typeof(T)</c> per closed generic so hot paths avoid the repeated metadata cost.
/// The JIT specializes one instance per <typeparamref name="T"/>, so <see cref="Type"/> is read
/// from a static field rather than re-evaluated on each call.
/// </summary>
public static class TypeCache<T>
{
    /// <summary>The cached <see cref="System.Type"/> for <typeparamref name="T"/>.</summary>
    public static readonly Type Type = typeof(T);
}
