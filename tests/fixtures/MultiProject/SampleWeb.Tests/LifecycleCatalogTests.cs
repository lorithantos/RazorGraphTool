using SampleLib;
using Xunit;

namespace SampleWeb.Tests;

/// <summary>
/// Fixture only — never executed. The store is exercised in InitializeAsync, the
/// xUnit pattern where a test's coverage flows through lifecycle setup rather
/// than the test body. Preload must end up covered by Preload_CountsCatalogs
/// even though no test method calls it.
/// </summary>
public class LifecycleCatalogTests : IAsyncLifetime
{
    private int _count;

    public Task InitializeAsync()
    {
        _count = new CatalogStore().Preload();
        return Task.CompletedTask;
    }

    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public void Preload_CountsCatalogs()
    {
        Assert.Equal(2, _count);
    }
}
