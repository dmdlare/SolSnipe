namespace SolSnipe.Core.Helpers;

public static class RetryHelper
{
    public static async Task<T?> RetryAsync<T>(
        Func<Task<T>> action,
        int maxAttempts = 3,
        int delayMs = 1000,
        double backoff = 2.0)
    {
        for (int i = 0; i < maxAttempts; i++)
        {
            try
            {
                return await action();
            }
            catch (Exception) when (i < maxAttempts - 1)
            {
                await Task.Delay((int)(delayMs * Math.Pow(backoff, i)));
            }
        }
        return default;
    }

    public static async Task RetryAsync(
        Func<Task> action,
        int maxAttempts = 3,
        int delayMs = 1000,
        double backoff = 2.0)
    {
        for (int i = 0; i < maxAttempts; i++)
        {
            try
            {
                await action();
                return;
            }
            catch (Exception) when (i < maxAttempts - 1)
            {
                await Task.Delay((int)(delayMs * Math.Pow(backoff, i)));
            }
        }
    }
}