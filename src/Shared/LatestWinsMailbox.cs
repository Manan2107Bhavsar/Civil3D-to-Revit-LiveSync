namespace LiveSync.Shared;

/// <summary>
/// One pending item per key; a newer post replaces an unconsumed older one.
/// The pipe thread posts, the Revit API thread (ExternalEvent) takes. If Civil 3D sends
/// v12 and v13 while Revit is busy, only v13 is applied: each payload is a full
/// replacement, so applying v12 first would be wasted work.
/// </summary>
public sealed class LatestWinsMailbox<T> where T : class
{
    private readonly Dictionary<string, T> _pending = new(StringComparer.OrdinalIgnoreCase);
    private readonly Func<T, long> _version;
    private readonly object _gate = new();

    public LatestWinsMailbox(Func<T, long> version) => _version = version;

    public enum PostResult
    {
        /// <summary>Nothing was pending for the key.</summary>
        Added,
        /// <summary>An older pending item was dropped in favour of this one.</summary>
        Replaced,
        /// <summary>A newer (or equal) version is already pending; this one was dropped.</summary>
        Stale,
    }

    /// <param name="displaced">The dropped item for Replaced/Stale; null for Added.</param>
    public PostResult Post(string key, T item, out T? displaced)
    {
        lock (_gate)
        {
            if (!_pending.TryGetValue(key, out var existing))
            {
                _pending[key] = item;
                displaced = null;
                return PostResult.Added;
            }
            if (_version(item) <= _version(existing))
            {
                displaced = item;
                return PostResult.Stale;
            }
            _pending[key] = item;
            displaced = existing;
            return PostResult.Replaced;
        }
    }

    public bool TryTake(string key, out T item)
    {
        lock (_gate)
        {
            if (_pending.Remove(key, out var found)) { item = found; return true; }
            item = null!;
            return false;
        }
    }

    /// <summary>Removes and returns everything pending.</summary>
    public List<KeyValuePair<string, T>> TakeAll()
    {
        lock (_gate)
        {
            var all = _pending.ToList();
            _pending.Clear();
            return all;
        }
    }

    public int Count
    {
        get { lock (_gate) return _pending.Count; }
    }
}
