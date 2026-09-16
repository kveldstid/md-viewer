namespace MdViewer.Core.Session;

/// <summary>
/// A capped, de-duplicated, most-recent-first list of documents
/// (SPECIFICATION.md 5.14).
///
/// Entries are identified by canonical path, compared case-insensitively on
/// Windows and case-sensitively elsewhere, because that is how the two file
/// systems actually behave — treating them the same way produces either
/// duplicate entries on Windows or wrongly merged ones on Linux.
///
/// Missing files are NOT pruned here. Startup must never block on touching the
/// file system (SPECIFICATION.md 5.13), so existence is checked lazily at
/// display time and a missing entry is shown as unavailable rather than removed
/// behind the user's back.
/// </summary>
public sealed class RecentDocumentList
{
    /// <summary>
    /// The history is deeper than the handful the start page shows at rest. The
    /// start page reveals the rest on demand, so a larger cap costs nothing in
    /// chrome while making the list useful beyond the last few minutes of work.
    /// </summary>
    public const int DefaultCapacity = 20;

    private static readonly StringComparer PathComparer =
        OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

    private readonly List<RecentDocument> _items = new();

    public RecentDocumentList(int capacity = DefaultCapacity)
    {
        if (capacity < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(capacity), capacity, "Capacity must be at least 1.");
        }

        Capacity = capacity;
    }

    public int Capacity { get; }

    /// <summary>Most recently opened first.</summary>
    public IReadOnlyList<RecentDocument> Items => _items;

    public int Count => _items.Count;

    /// <summary>
    /// Records that a document was opened: inserts it at the front, or moves an
    /// existing entry there and replaces its metadata. Trims to <see cref="Capacity"/>.
    /// </summary>
    public void Touch(RecentDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);

        _items.RemoveAll(i => PathComparer.Equals(i.Path, document.Path));
        _items.Insert(0, document);

        if (_items.Count > Capacity)
        {
            _items.RemoveRange(Capacity, _items.Count - Capacity);
        }
    }

    /// <summary>
    /// Updates the remembered reading position of an entry without disturbing
    /// the order: where the reader is in a document says nothing about how
    /// recently they opened it.
    /// </summary>
    public bool UpdateScrollOffset(string path, int scrollOffset)
    {
        ArgumentNullException.ThrowIfNull(path);

        var index = _items.FindIndex(i => PathComparer.Equals(i.Path, path));
        if (index < 0) return false;

        _items[index] = _items[index] with { ScrollOffset = Math.Max(0, scrollOffset) };
        return true;
    }

    public bool Remove(string path)
    {
        ArgumentNullException.ThrowIfNull(path);
        return _items.RemoveAll(i => PathComparer.Equals(i.Path, path)) > 0;
    }

    public void Clear() => _items.Clear();

    /// <summary>Replaces the contents, e.g. when restoring a session.</summary>
    public void Restore(IEnumerable<RecentDocument> documents)
    {
        ArgumentNullException.ThrowIfNull(documents);

        _items.Clear();
        foreach (var document in documents)
        {
            if (_items.Count >= Capacity) break;
            if (_items.Any(i => PathComparer.Equals(i.Path, document.Path))) continue;
            _items.Add(document);
        }
    }
}
