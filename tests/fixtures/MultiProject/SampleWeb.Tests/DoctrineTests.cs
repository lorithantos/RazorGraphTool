using SampleLib;
using Xunit;

namespace SampleWeb.Tests;

/// <summary>
/// Fixture only — never executed. The twin of GreeterTests, for the other
/// dispatch mechanism: every call here binds to the ABSTRACT declaration, so
/// the only way coverage reaches either override is across the override
/// relation. Nothing in this file names PatrolDoctrine.Advance or
/// EscortDoctrine.Advance.
/// </summary>
public class DoctrineTests
{
    [Fact]
    public void Advance_ThroughTheBaseDeclaration()
    {
        Doctrine doctrine = new PatrolDoctrine();

        Assert.Equal(4, doctrine.Advance(3));
    }

    [Fact]
    public void Advance_ThroughARunnerThatHoldsTheBase()
    {
        Assert.Equal(5, DoctrineRunner.Run(new EscortDoctrine(), 3));
    }
}
