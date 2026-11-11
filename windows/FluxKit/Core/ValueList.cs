using System.Collections;

namespace FluxKit;

// A read-only list that compares by its items, so that records with lists
// compare by value. The UI publishes a state only when it changed.
public sealed class ValueList<T> : IReadOnlyList<T>, IEquatable<ValueList<T>>
{
    readonly T[] items;

    public static readonly ValueList<T> Empty = new([]);

    public ValueList(IEnumerable<T> items) => this.items = items.ToArray();

    public T this[int index] => items[index];
    public int Count => items.Length;

    public IEnumerator<T> GetEnumerator() => ((IEnumerable<T>)items).GetEnumerator();
    IEnumerator IEnumerable.GetEnumerator() => items.GetEnumerator();

    public bool Equals(ValueList<T>? other) => other is not null && items.AsSpan().SequenceEqual(other.items);
    public override bool Equals(object? obj) => obj is ValueList<T> other && Equals(other);

    public override int GetHashCode()
    {
        var hash = new HashCode();
        foreach (var item in items)
        {
            hash.Add(item);
        }
        return hash.ToHashCode();
    }
}

public static class ValueList
{
    public static ValueList<T> From<T>(IEnumerable<T> items) => new(items);
}
