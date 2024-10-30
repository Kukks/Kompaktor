using System.Collections;
using System.Collections.Concurrent;

namespace Kompaktor.Utils;

public class ConcurrentHashSet<T> : IEnumerable<T> where T : notnull
{
    private readonly ConcurrentDictionary<T, byte> _dictionary;

    public ConcurrentHashSet()
    {
        _dictionary = new ConcurrentDictionary<T, byte>();
    }

    const byte DummyByte = byte.MinValue;

    // For convenience, we add HashSet equivalent APIs here...
    public bool Contains(T item) => _dictionary.ContainsKey(item);
    public bool Add(T item) => _dictionary.TryAdd(item, DummyByte);
    public bool Remove(T item) => _dictionary.TryRemove(item, out _);

    public IEnumerator<T> GetEnumerator()
    {
        return _dictionary.Keys.GetEnumerator();
    }

    IEnumerator IEnumerable.GetEnumerator()
    {
        return GetEnumerator();
    }
    
    public int Count => _dictionary.Count;
}