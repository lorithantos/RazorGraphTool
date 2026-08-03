namespace SampleLib;

/// <summary>
/// Async-disposable resource for the await-using coverage case: DisposeAsync is
/// only ever invoked implicitly, by an await using in a test's lifecycle setup.
/// </summary>
public sealed class CatalogSession : IAsyncDisposable
{
    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
