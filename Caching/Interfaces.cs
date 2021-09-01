using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Caching
{
    public interface IReadOnlyCache<TKey,TValue>
        where TKey: IEquatable<TKey>
    {
        bool TryGet(TKey key, out TValue value);
    }

    public interface ICache<TKey,TValue> : IReadOnlyCache<TKey,TValue>
        where TKey: IEquatable<TKey>
    {
        void Add(TKey key, TValue value);
        void Set(TKey key, TValue value);
    }

    public interface ILfuCache<TKey,TValue>: ICache<TKey,TValue>
        where TKey: IEquatable<TKey>
    {
        void ReduceLfuAccessCounts(ulong dividend, ulong minimum);
    }
}
