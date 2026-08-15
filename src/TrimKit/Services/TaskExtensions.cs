namespace TrimKit.Services;

/// <summary>
/// Extension methods for safely handling fire-and-forget async operations.
/// </summary>
public static class TaskExtensions
{
    /// <summary>
    /// Safely fires and forgets a Task, catching and logging any exceptions
    /// instead of silently swallowing them.
    /// </summary>
    public static async void SafeFireAndForget(this Task task, ILogService? logService = null, string? context = null)
    {
        try
        {
            await task;
        }
        catch (Exception ex)
        {
            var msg = string.IsNullOrEmpty(context)
                ? $"Background task failed: {ex.Message}"
                : $"{context}: {ex.Message}";
            logService?.Log(Models.LogLevel.Warning, msg);
        }
    }
}
