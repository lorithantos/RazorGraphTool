namespace SampleLib;

/// <summary>
/// Async-disposable resource for the await-using coverage case: DisposeAsync is
/// only ever invoked implicitly, by an await using in a test's lifecycle setup.
/// The explicit constructor is the production-ctor coverage case — a test that
/// news up this type exercises the ctor and, through it, Open.
/// </summary>
public sealed class CatalogSession : IAsyncDisposable
{
    public CatalogSession() => Open();

    /// <summary>Reachable only through the constructor — the depth-2-via-ctor case.</summary>
    private static void Open() { }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
