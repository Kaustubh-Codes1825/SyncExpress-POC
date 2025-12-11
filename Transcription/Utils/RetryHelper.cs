using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace Transcription.Utils
{
    public static class RetryHelper
    {
        public static async Task<(T result, int attempts, List<string> logs)> RetryAsync<T>(
            Func<Task<T>> operation,
            ILogger logger,
            int maxAttempts = 5,
            int baseDelayMs = 500,
            CancellationToken ct = default)
        {
            var random = new Random();
            Exception? lastException = null;
            var logs = new List<string>();

            for (int attempt = 1; attempt <= maxAttempts; attempt++)
            {
                try
                {
                    var startMsg = $"Attempt {attempt}/{maxAttempts}: starting.";
                    logs.Add(startMsg);
                    logger?.LogInformation(startMsg);

                    ct.ThrowIfCancellationRequested();
                    T result = await operation().ConfigureAwait(false);

                    if (attempt > 1)
                    {
                        var successMsg = $"Attempt {attempt}/{maxAttempts}: succeeded.";
                        logs.Add(successMsg);
                        logger?.LogInformation(successMsg);
                    }

                    return (result, attempt, logs);
                }
                catch (Exception ex) when (IsTransient(ex))
                {
                    lastException = ex;

                    if (attempt == maxAttempts)
                    {
                        var failMsg = $"Attempt {attempt}/{maxAttempts}: failed with transient error. No more retries. Error: {ex.Message}";
                        logs.Add(failMsg);
                        logger?.LogError(ex, failMsg);
                        break;
                    }

                    // exponential backoff + jitter
                    int maxDelay = baseDelayMs * (int)Math.Pow(2, attempt - 1);
                    int delay = random.Next(baseDelayMs, Math.Max(baseDelayMs + 1, maxDelay));

                    var warnMsg = $"Attempt {attempt}/{maxAttempts}: failed with transient error: {ex.Message}. Retrying in {delay} ms...";
                    logs.Add(warnMsg);
                    logger?.LogWarning(ex, warnMsg);

                    try
                    {
                        await Task.Delay(delay, ct).ConfigureAwait(false);
                    }
                    catch (TaskCanceledException)
                    {
                        // cancellation requested — bubble up
                        throw;
                    }
                }
                catch (Exception ex)
                {
                    // Non-transient error — log and rethrow; record final message
                    var nonTransientMsg = $"Attempt {attempt}/{maxAttempts}: failed with non-transient error: {ex.Message}. Aborting.";
                    logs.Add(nonTransientMsg);
                    logger?.LogError(ex, nonTransientMsg);
                    throw;
                }
            }

            // if we reach here retries exhausted
            throw lastException ?? new Exception("Operation failed after retries and no exception was captured.");
        }

        private static bool IsTransient(Exception ex)
        {
            if (ex is TaskCanceledException) return true;          // timeout
            if (ex is HttpRequestException) return true;           // network issues / 5xx mapped
            return false;
        }
    }
}
