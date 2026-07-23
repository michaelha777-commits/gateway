public static class ValueTaskExtensions
{
    public static Task WaitAsync(this ValueTask valueTask, TimeSpan timeout, CancellationToken cancellationToken = default)
        => valueTask.AsTask().WaitAsync(timeout, cancellationToken);

    public static Task<T> WaitAsync<T>(this ValueTask<T> valueTask, TimeSpan timeout, CancellationToken cancellationToken = default)
        => valueTask.AsTask().WaitAsync(timeout, cancellationToken);
}
