using Janet.Mcp.Security;
using Microsoft.Extensions.DependencyInjection;

namespace RazorGraph.Mcp;

/// <summary>
/// Whether the HTTP server has its bearer check yet. The server binds first and builds the check
/// afterwards, because building it runs the token provider -- for --auth-token-command, a process
/// start -- and a port left unbound while that runs is refused, which a client cannot tell from
/// no server at all. While this is not ready the server answers 503: /health says "starting", and
/// every other route says to retry.
/// </summary>
/// <remarks>
/// <see cref="BearerAuthenticator"/> asks its provider in its constructor, so "building the check"
/// and "reading the token" are one step, taken on a background task once the server has bound.
/// It completes whether the provider succeeded or failed: a provider that throws leaves the check
/// with no token and the error kept for /health, as it always has, and the server ready with it.
/// </remarks>
internal sealed class TransportReadiness(IServiceProvider services)
{
    private readonly TaskCompletionSource<BearerAuthenticator> ready =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    private int begun;

    /// <summary>
    /// The bearer check, once built. Faults only if building it does, which a provider failure
    /// does not: the authenticator keeps that for its snapshot.
    /// </summary>
    public Task<BearerAuthenticator> Ready => ready.Task;

    /// <summary>True once <see cref="Ready"/> has completed.</summary>
    public bool IsReady => ready.Task.IsCompleted;

    /// <summary>
    /// Builds the bearer check on a background task, off every request's path. Called once the
    /// server has bound; a second call does nothing.
    /// </summary>
    public void Begin()
    {
        if (Interlocked.Exchange(ref begun, 1) != 0)
        {
            return;
        }

        _ = Task.Run(() =>
        {
            try
            {
                // Resolved through the container, which built it from its factory and so owns it:
                // disposing the app disposes the authenticator and stops its refresh consumer.
                ready.TrySetResult(services.GetRequiredService<BearerAuthenticator>());
            }
            catch (Exception ex)
            {
                ready.TrySetException(ex);
            }
        });
    }
}
