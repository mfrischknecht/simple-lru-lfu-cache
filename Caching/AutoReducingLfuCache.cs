using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Caching
{
    public sealed class AutoReducingLfuCache<TKey, TValue> : ICache<TKey, TValue>
        where TKey : IEquatable<TKey>
    {
        private readonly ILfuCache<TKey, TValue> _cache;
        private readonly ulong _reductionDividend;
        private readonly ulong _reductionMinimum;
        private readonly uint  _accessesBetweenReductions;
        private long _accessCount;

        public AutoReducingLfuCache(
             ILfuCache<TKey, TValue> cache,
             uint accessesBetweenReductions,
             ulong dividend,
             ulong minimum)
        {
            _cache = cache ?? throw new ArgumentNullException(nameof(cache));

            if (accessesBetweenReductions == 0) throw new ArgumentOutOfRangeException(nameof(accessesBetweenReductions));
            if (dividend                  == 0) throw new ArgumentOutOfRangeException(nameof(dividend));
            _accessesBetweenReductions = accessesBetweenReductions;
            _reductionDividend = dividend;
            _reductionMinimum  = minimum;
        }

        public void Add(TKey key, TValue value)
        {
            IncrementAccessCount();
            _cache.Add(key, value);
        }

        public void Set(TKey key, TValue value)
        {
            IncrementAccessCount();
            _cache.Set(key, value);
        }

        public bool TryGet(TKey key, out TValue value)
        {
            IncrementAccessCount();
            return _cache.TryGet(key, out value);
        }

        private void IncrementAccessCount()
        {
            var newCount = Interlocked.Increment(ref _accessCount);
            if (newCount % _accessesBetweenReductions != 0) return;
            _cache.ReduceLfuAccessCounts(_reductionDividend, _reductionMinimum);
        }
    }
}
