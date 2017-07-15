using System;
using System.Collections.Immutable;
using System.Threading;

namespace Yllibed.StreamMultiplexer.Core
{
	public static class Transactional
	{
		public static T Update<T, TState>(ref T value, TState state, Func<T, TState, T> updater)
			where T : class
		{
			while (true)
			{
				var capture = value;
				var updated = updater(capture, state);
				if (Interlocked.CompareExchange(ref value, updated, capture) == capture)
				{
					return updated;
				}
			}
		}

		public static ImmutableDictionary<TKey, TValue> SetItem<TKey, TValue>(ref ImmutableDictionary<TKey, TValue> dictionary, TKey key, TValue value)
		{
			while (true)
			{
				var capture = dictionary;
				var updated = capture.SetItem(key, value);
				if (Interlocked.CompareExchange(ref dictionary, updated, capture) == capture)
				{
					return updated;
				}
			}
		}

		public static ImmutableDictionary<TKey, TValue> Remove<TKey, TValue>(ref ImmutableDictionary<TKey, TValue> dictionary, TKey key)
		{
			while (true)
			{
				var capture = dictionary;
				var updated = capture.Remove(key);
				if (Interlocked.CompareExchange(ref dictionary, updated, capture) == capture)
				{
					return updated;
				}
			}
		}
	}
}
