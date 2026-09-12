using SampleLib;
using Xunit;

namespace SampleWeb.Tests;

/// <summary>
/// Fixture only — never executed. The twin of GreeterTests for a PROPERTY.
/// The read binds to ISink.Channel and nothing here names LedgerSink.Channel,
/// so the implementation is reachable only across the implementation relation —
/// which carried no edge for properties at all until 2026-09-11.
/// </summary>
public class SinkTests
{
    [Fact]
    public void Channel_ThroughTheInterface()
    {
        ISink sink = new LedgerSink();

        Assert.Equal("ledger stream", Read(sink));
    }

    /// <summary>One hop past the interface read, so depth keeps counting.</summary>
    private static string Read(ISink sink) => sink.Channel;
}
