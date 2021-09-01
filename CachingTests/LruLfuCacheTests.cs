using Caching;
using System;
using Xunit;

namespace CachingTests
{
    public class LruLfuCacheTests
    {
        [Fact]
        public void New_cache_is_empty()
        {
            var cache = new LruLfuCache<string, int>(1,1);
            Assert.Equal(0, cache.Count);
        }

        [Fact]
        public void Null_keys_cannot_be_cached()
        {
            var cache = new LruLfuCache<string, int>(1,1);
            Assert.Throws<ArgumentNullException>(() => cache.Add   (null, 0));
            Assert.Throws<ArgumentNullException>(() => cache.Set   (null, 0));
            Assert.Throws<ArgumentNullException>(() => cache.TryGet(null, out var _));
        }

        [Fact]
        public void Add_throws_if_entry_already_exists()
        {
            var cache = new LruLfuCache<string, int>(1,1);
            cache.Add("0", 0);
            Assert.Throws<ArgumentException>(() => cache.Add("0", 1));
            Assert.True(cache.TryGet("0", out var value));
            Assert.Equal(0, value);
        }

        [Fact]
        public void Looking_up_unknown_entries_fails()
        {
            var cache = new LruLfuCache<string, int>(1,1);
            Assert.False(cache.TryGet("1", out var intValue));
            Assert.Equal(default, intValue);

            var cache2 = new LruLfuCache<string, object>(1,1);
            Assert.False(cache2.TryGet("1", out var objValue));
            Assert.Null(objValue);
        }

        [Fact]
        public void Cache_with_single_lfu_and_lru_slots_stores_only_one_element()
        {
            var cache = new LruLfuCache<string, int>(1,1);

            cache.Add("1", 1);
            Assert.Equal(1, cache.Count);
            Assert.True(cache.TryGet("1", out var value));
            Assert.Equal(1, value);

            cache.Add("2", 2);
            Assert.Equal(1, cache.Count);
            Assert.False(cache.TryGet("1", out value));
            Assert.True(cache.TryGet("2", out value));
            Assert.Equal(2, value);
        }

        [Fact]
        public void Cache_keeps_entries_that_fall_out_of_lru_if_they_are_referenced_by_lfu()
        {
            var cache = new LruLfuCache<string, int>(10,10);

            for (var i = 0; i < 100; ++i)
                cache.Set("0", 0);

            for (var i = 1; i < 100; ++i)
                cache.Add($"{i}", i);

            Assert.True(cache.TryGet("0", out var value));
            Assert.Equal(0, value);
        }

        [Fact]
        public void Cache_keeps_entries_that_fall_out_of_lfu_if_they_are_referenced_by_lru()
        {
            var cache = new LruLfuCache<string, int>(10,10);

            for (var i = 1; i < 10; ++i)
            {
                for (var j = 0; j < 100; ++j)
                    cache.Set($"{i}", i);

                // "0" is the last accessed entry, but every other
                // entry has been accessed much more, so "0" never
                // rises in the LFU list.
                cache.Set("0", 0);
            }

            cache.Set($"101", 101); //Purge "0" from the lowest LFU slot

            Assert.True(cache.TryGet("0", out var value));
            Assert.Equal(0, value);
        }

        [Fact]
        public void Cache_removes_entries_once_they_fall_out_of_both_lru_and_lfu()
        {
            var cache = new LruLfuCache<string, int>(10,10);

            for (var i = 0; i < 100; ++i)
                cache.Set($"{i}", i);

            // The LFU cache will contain the numbers 0..8
            // as well as 99 (since alle entries have an acces count of 1,
            // all entries past 8 will only "churn" on the last slot).
            // OTOH, the LRU cache will contain the numbers 90..99.
            // All other numbers aren't in the cache anymore.

            Assert.False(cache.TryGet("9", out var _));
            Assert.False(cache.TryGet("50", out var _));
            Assert.False(cache.TryGet("89", out var _));
        }

        [Fact]
        public void Cache_retains_oldest_lfu_entries_if_all_items_are_accessed_the_same_amount()
        {
            var cache = new LruLfuCache<string, int>(10,10);

            for (var i = 0; i < 100; ++i)
                cache.Set($"{i}", i);

            //Note: Only the first 9 entries are still in the LFU set,
            //      as the last spot is exchanged with each new entry.
            for (var i = 0; i < 9; ++i)
                Assert.True(cache.TryGet($"{i}", out var _));
        }

        [Fact]
        public void Cache_retains_the_most_current_lru_entries()
        {
            var cache = new LruLfuCache<string, int>(10,10);

            for (var i = 0; i < 100; ++i)
                cache.Set($"{i}", i);

            for (var i = 90; i < 100; ++i)
                Assert.True(cache.TryGet($"{i}", out var _));
        }

        [Fact]
        public void ReduceAccessCounts_does_not_remove_enries_from_cache_even_if_counts_reach_zero()
        {
            var cache = new LruLfuCache<string, int>(100,100);

            for (var i = 0; i < 100; ++i)
                cache.Set($"{i}", i);

            Assert.Equal(100, cache.Count);

            cache.ReduceLfuAccessCounts(1, 100);

            Assert.Equal(100, cache.Count);
        }

        [Fact]
        public void ReduceAccessCounts_reduces_access_counts()
        {
            const int ENTRIES = 100;

            var cache = new LruLfuCache<string, int>(1, ENTRIES);

            for (var i = 0; i < ENTRIES; ++i)
                for (var j = 0; j < 10; ++j)
                    cache.Set($"{i}", i);

            cache.ReduceLfuAccessCounts(1, 100);

            for (var i = ENTRIES; i < 2*ENTRIES; ++i)
                cache.Set($"{i}", i);

            for (var i = 0; i < ENTRIES; ++i)
                Assert.False(cache.TryGet($"{i}", out var _));
        }
    }
}
