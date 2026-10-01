using System.Collections.Concurrent;

namespace DelicateCouriers.ApiService.Infrastructure.Services
{
    /// <summary>
    /// Rate limiter for WooCommerce API calls
    /// Prevents exceeding WooCommerce's 600 requests per minute limit
    /// Thread-safe for concurrent requests from 100-200 users
    /// </summary>
    public class WooCommerceRateLimiter
    {
        // Track request timestamps per store
        private readonly ConcurrentDictionary<int, Queue<DateTime>> _requestTimestamps = new();
        private readonly SemaphoreSlim _semaphore = new(1, 1);

        // WooCommerce default limit: 600 requests per minute
        private const int MaxRequestsPerMinute = 600;
        private readonly TimeSpan _timeWindow = TimeSpan.FromMinutes(1);

        /// <summary>
        /// Wait if necessary to respect rate limits, then allow request
        /// Returns immediately if under limit, delays if approaching limit
        /// </summary>
        /// <param name="storeId">Store ID to track rate limit per store</param>
        public async Task WaitIfNeededAsync(int storeId)
        {
            await _semaphore.WaitAsync();
            try
            {
                // Get or create queue for this store
                var queue = _requestTimestamps.GetOrAdd(storeId, _ => new Queue<DateTime>());

                // Remove timestamps older than 1 minute
                var cutoffTime = DateTime.UtcNow.Subtract(_timeWindow);

                while (queue.Count > 0 && queue.Peek() < cutoffTime)
                {
                    queue.Dequeue();
                }

                // If we're at the limit, calculate how long to wait
                if (queue.Count >= MaxRequestsPerMinute)
                {
                    var oldestRequest = queue.Peek();
                    var waitTime = oldestRequest.Add(_timeWindow) - DateTime.UtcNow;

                    if (waitTime > TimeSpan.Zero)
                    {
                        Console.WriteLine($"Rate limit approaching for Store {storeId}. Waiting {waitTime.TotalSeconds:F1}s");
                        await Task.Delay(waitTime);

                        // After waiting, remove old timestamps
                        cutoffTime = DateTime.UtcNow.Subtract(_timeWindow);

                        while (queue.Count > 0 && queue.Peek() < cutoffTime)
                        {
                            queue.Dequeue();
                        }
                    }
                }

                // Add current request timestamp
                queue.Enqueue(DateTime.UtcNow);
            }
            finally
            {
                _semaphore.Release();
            }
        }

        /// <summary>
        /// Get current request count for a store in the last minute
        /// Useful for monitoring and debugging
        /// </summary>
        public int GetCurrentRequestCount(int storeId)
        {
            if (!_requestTimestamps.TryGetValue(storeId, out var queue))
            {
                return 0;
            }

            var cutoffTime = DateTime.UtcNow.Subtract(_timeWindow);
            
            return queue.Count(t => t >= cutoffTime);
        }

        /// <summary>
        /// Clear rate limit tracking for a specific store
        /// Useful for testing or manual reset
        /// </summary>
        public void ResetStore(int storeId)
        {
            _requestTimestamps.TryRemove(storeId, out _);
        }
    }
}