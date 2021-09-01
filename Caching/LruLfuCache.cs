using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;

namespace Caching
{
    /// <summary>
    /// Caches values (<typeparamref name="TValue"/>) for associated keys
    /// (<typeparamref name="TKey"/>) applying both LRU and LFU as cache 
    /// eviction strategies.
    /// </summary>
    /// <remarks>
    /// <see cref="LruLfuCache{TKey,TValue}"/> instances can be initialized with 
    /// separate <see cref="LfuCapacity"/> and <see cref="LruCapacity"/> values.
    /// The amount of entries cached by a cache instance can vary from the
    /// higher of both capacities up to the sum of both, depending on the
    /// access pattern an application exhibits towards the cache.
    /// 
    /// For the LFU cache, each reading and writing access counts to the
    /// frequency count of each entry (even those outside of the LFU cache,
    /// so long as they are still contained in the internal LRU cache).
    /// 
    /// In order to achieve a more time window oriented LFU cache behavior,
    /// applications can call the <see cref="ReduceLfuAccessCounts(ulong,ulong)"/>
    /// method, which will reduce the access count of *all* entries by a fraction
    /// and/or a set amount.
    /// </remarks>
    public sealed class LruLfuCache<TKey, TValue> : ILfuCache<TKey,TValue>
        where TKey: IEquatable<TKey>
    {
        /// <summary>
        /// Used to make sure only one thread accesses the <see cref="LruLfuCache{TKey,TValue}"/>'s
        /// internal state at any given time.
        /// </summary>
        private readonly object _lock = new object();

        /// <summary>Stores all cached entries on a by-key basis.</summary>
        private readonly Dictionary<TKey,Entry> _entries = new Dictionary<TKey,Entry>();

        /// <summary>The capacity of the internal LFU cache.</summary>
        public readonly uint LfuCapacity;

        /// <summary>Stores the most frequently accessed entries (according to their access counts)</summary>
        /// <remarks>
        /// New entries are added to the beginning of the list (but might potentially
        /// skip existing entries due to previous calls to
        /// <see cref="ReduceLfuAccessCounts(ulong,ulong)"/>). The last entries in the
        /// list are the ones with the highest access counts.
        /// </remarks>
        private readonly LinkedList<Entry> _lfuEntries = new LinkedList<Entry>();

        /// <summary>The capacity of the internal LRU cache.</summary>
        public readonly uint LruCapacity;

        /// <summary>Stores the most recently accessed entries.</summary>
        /// <remarks>
        /// New entries are added to the beginning of the list; the entries at the end
        /// are the ones that were accessed the longest time ago (and might soon be dropped).
        /// </remarks>
        private readonly LinkedList<Entry> _lruEntries = new LinkedList<Entry>();

        /// <summary>Create a <see cref="LruLfuCache{TKey,TValue}"/> with the specified capacities</summary>
        /// <param name="lruCapacity">The number of most recently accessed elements that are retained</param>
        /// <param name="lfuCapacity">The number of most frequently accessed elements that are retained</param>
        public LruLfuCache(uint lruCapacity, uint lfuCapacity)
        {
            if (lruCapacity < 1) throw new ArgumentNullException(nameof(lruCapacity));
            if (lfuCapacity < 1) throw new ArgumentNullException(nameof(lfuCapacity));
            LruCapacity = lruCapacity;
            LfuCapacity = lfuCapacity;
        }

        /// <summary>The number of entries currently retained by the cache</summary>
        public int Count { get { lock (_lock) { return _entries.Count; } } }

        /// <summary>Add a new entry to the cache.</summary>
        /// <exception cref="ArgumentException">
        /// The provided <paramref name="key"/> is already present in the cache.
        /// </exception>
        /// <exception cref="ArgumentNullException">
        /// The provided <paramref name="key"/> is <see langword="null"/>.
        /// </exception>
        public void Add(TKey key, TValue value)
        {
            var entry = new Entry(key, value);

            lock (_lock)
            {
                //Might throw if the key is already in the cache
                _entries.Add(key, entry);
                IncrementAccessCount(entry);
                RefreshAccessRecency(entry);
            }
        }

        /// <summary>
        /// Set the entry for a given <paramref name="key"/> to the specified
        /// <paramref name="value"/>.
        /// </summary>
        /// <remarks>
        /// If the provided <paramref name="key"/> is already in the cache, its
        /// respective <paramref name="value"/> will be overwritten; if it is not,
        /// a new entry will be added.
        /// </remarks>
        /// <exception cref="ArgumentNullException">
        /// The provided <paramref name="key"/> is <see langword="null"/>.
        /// </exception>
        public void Set(TKey key, TValue value)
        {
            lock (_lock)
            {
                if (TryGetEntry(key, out var entry))
                    entry.Value = value;
                else
                    Add(key, value);
            }
        }

        /// <summary>
        /// Attempts to retrieve the <paramref name="value"/> for a given
        /// <paramref name="key"/>.
        /// </summary>
        /// <param name="key">The key for which the lookup is performed</param>
        /// <param name="value">
        /// Receives the cached value if the key is present in the cache;
        /// is set to <see langword="default"/> otherwise.
        /// </param>
        /// <returns>
        /// <see langword="true"/> if the provided <paramref name="key"/> is
        /// present in the cache and <paramref name="value"/> has been retrieved,
        /// <see langword="false"/> otherwise.
        /// </returns>
        /// <exception cref="ArgumentNullException">
        /// The provided <paramref name="key"/> is <see langword="null"/>.
        /// </exception>
        public bool TryGet(TKey key, out TValue value)
        {
            value = default;
            if (!TryGetEntry(key, out var entry)) return false;
            value = entry.Value;
            return true;
        }

        /// <summary>
        /// Reduces the access counts of all cached entries, which allows
        /// phasing entries out of the internal LFU cache over time.
        /// </summary>
        /// <remarks>
        /// The parameters <paramref name="dividend"/> and <paramref name="minimum"/>
        /// are used to affect the amount by which each entry's access count
        /// is reduced.
        /// 
        /// This operation never alters the content of the LFU internal cache
        /// of a <see cref="LruLfuCache{TKey,TValue}"/> instance; all entries will
        /// still remain in the cache (even if their access count hits zero)
        /// and their order in the cache will be the same. However, the reduction
        /// operation could potentially heavily influence the weight of upcoming
        /// access operations, as the access counts of existing entries might
        /// have been reduced to zero.
        /// </remarks>
        /// <param name="dividend">
        /// The dividend of the current access count that will be deduced.
        /// That is: If <paramref name="dividend"/> is 1, the access count
        /// will be reduced by 1/1th (100%), if it is 100, it will be reduced
        /// by roughly 1/100th (1%).
        /// 
        /// The deduced amount is calculated through an integer division, so 
        /// e.g. an access count of 1 will not be reduced to zero unless the 
        /// dividend is set to 1.
        /// 
        /// A dividend of zero (0) means that no proportional amount will be
        /// deduced from the individual entry's access count.
        /// </param>
        /// <param name="minimum">
        /// The minimum amount that will be deduced from each entry's access
        /// count. That is: With a <paramref name="dividend"/> of 100 and a
        /// <paramref name="minimum"/> of 10, an entry with an access count
        /// of 100 will be reduced to 90 (instead of 99).
        /// 
        /// Note that the access count of each entry cannot fall below zero.
        /// </param>
        public void ReduceLfuAccessCounts(ulong dividend, ulong minimum)
        {
            lock (_lock)
            {
                foreach (var entry in _entries.Values)
                {
                    var minuend = 0ul;
                    if (dividend > 0) minuend = entry.AccessCount / dividend;
                    minuend = Math.Max(minuend, minimum);
                    minuend = Math.Min(minuend, entry.AccessCount);
                    entry.AccessCount -= minuend;
                }
            }
        }

        /// <summary>
        /// Tries to access a given entry and updates the respective
        /// entry's recency and access count if successful.
        /// </summary>
        private bool TryGetEntry(TKey key, out Entry entry)
        {
            lock (_lock)
            {
                if (!_entries.TryGetValue(key, out entry))
                    return false;

                IncrementAccessCount(entry);
                RefreshAccessRecency(entry);
                return true;
            }
        }

        /// <summary>
        /// Makes sure there's at least one empty slot
        /// in the internal LRU cache, potentially removing
        /// the oldest entry.
        /// </summary>
        private void ReserveLruSlot()
        {
            if (_lruEntries.Count < LruCapacity) return;

            var entry = _lruEntries.Last;
            _lruEntries.RemoveLast();

            entry.Value.LruEntry = null;
            if (entry.Value.LfuEntry == null)
                _entries.Remove(entry.Value.Key);
        }

        /// <summary>
        /// Makes sure there's at least one empty slot
        /// in the internal LFU cache, potentially removing
        /// the entry with the lowest access count.
        /// </summary>
        private void ReserveLfuSlot()
        {
            if (_lfuEntries.Count < LfuCapacity) return;

            var entry = _lfuEntries.First;
            _lfuEntries.RemoveFirst();

            entry.Value.LfuEntry = null;
            if (entry.Value.LruEntry == null)
                _entries.Remove(entry.Value.Key);
        }

        /// <summary>
        /// Increments an entry's access count, adding it to the
        /// internal LFU cache if it is not already present there.
        /// </summary>
        /// <remarks>
        /// Incrementing the access count of an entry will automatically
        /// propagate it ("bubble it up") to the slot before the first
        /// existing entry with an equal or greater access count.
        /// </remarks>
        private void IncrementAccessCount(Entry entry)
        {
            entry.AccessCount++;

            if (entry.LfuEntry == null)
            {
                ReserveLfuSlot();
                entry.LfuEntry = _lfuEntries.AddFirst(entry);
            }

            var insertBefore = entry.LfuEntry;
            do insertBefore = insertBefore.Next;
            while (insertBefore?.Value.AccessCount < entry.AccessCount);

            if (ReferenceEquals(insertBefore, entry.LfuEntry.Next)) return;

            _lfuEntries.Remove(entry.LfuEntry);
            if (insertBefore != null)
                _lfuEntries.AddBefore(insertBefore, entry.LfuEntry);
            else
                _lfuEntries.AddLast(entry.LfuEntry);
        }

        /// <summary>
        /// Flag an entry as having been the most recently accessed
        /// one by moving it to the start of the internal LRU cache.
        /// </summary>
        private void RefreshAccessRecency(Entry entry)
        {
            if (entry.LruEntry == null)
            {
                ReserveLruSlot();
                entry.LruEntry = _lruEntries.AddFirst(entry);
            }
            else if (!ReferenceEquals(_lruEntries.First, entry.LruEntry))
            {
                _lruEntries.Remove(entry.LruEntry);
                _lruEntries.AddFirst(entry.LruEntry);
            }
        }

        private sealed class Entry
        {
            public readonly TKey Key;
            public TValue Value;

            public ulong AccessCount;
            public LinkedListNode<Entry> LruEntry;
            public LinkedListNode<Entry> LfuEntry;

            public Entry(TKey key, TValue value)
            {
                Key = key;
                Value = value;
            }
        }
    }
}
