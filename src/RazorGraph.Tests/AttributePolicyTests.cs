namespace RazorGraph.Tests;

using RazorGraph.Extractor.Attributes;
using Xunit;

public class AttributePolicyTests
{
    /// <summary>
    /// The embedded default must carry exactly the name sets that were
    /// hardcoded in MethodRoles before the policy became data. This test IS the
    /// byte-identical-classification contract: as long as it holds, moving the
    /// sets out of code cannot have changed what any graph classifies.
    /// </summary>
    [Fact]
    public void Default_CarriesTheHistoricalNameSets()
    {
        var policy = AttributePolicy.Default;

        Assert.Equal(
            new[]
            {
                "DataTestMethodAttribute", "FactAttribute", "TestAttribute", "TestCaseAttribute",
                "TestCaseSourceAttribute", "TestMethodAttribute", "TheoryAttribute"
            },
            policy.TestAttributeNames.OrderBy(n => n, StringComparer.Ordinal));

        Assert.Equal(
            new[]
            {
                "ClassCleanupAttribute", "ClassInitializeAttribute", "OneTimeSetUpAttribute",
                "OneTimeTearDownAttribute", "SetUpAttribute", "TearDownAttribute",
                "TestCleanupAttribute", "TestInitializeAttribute"
            },
            policy.LifecycleAttributeNames.OrderBy(n => n, StringComparer.Ordinal));

        Assert.Equal(new[] { "ApiControllerAttribute" }, policy.ControllerAttributeNames);
        Assert.Equal(new[] { "BindPropertyAttribute" }, policy.BindPropertyAttributeNames);
    }

    /// <summary>
    /// The shipped default suppresses nothing and declares no registrations:
    /// uniform emission is the default, and any narrowing must be a visible
    /// line in a file somebody wrote.
    /// </summary>
    [Fact]
    public void Default_SuppressesNothing_AndDeclaresNoRegistrations()
    {
        Assert.Empty(AttributePolicy.Default.SuppressArgumentsFor);
        Assert.Empty(AttributePolicy.Default.Registrations);
    }

    [Fact]
    public void LoadFile_InheritsEverySectionTheOverrideOmits()
    {
        var path = WriteTemp("""
            {
              ".comment": ["Only narrows one attribute's payload; everything else is inherited."],
              "suppressArgumentsFor": { "names": ["Xunit.InlineDataAttribute"] }
            }
            """);

        var policy = AttributePolicy.LoadFile(path);

        Assert.Equal(new[] { "Xunit.InlineDataAttribute" }, policy.SuppressArgumentsFor);
        // The untouched sections are the default's, not empty.
        Assert.Equal(AttributePolicy.Default.TestAttributeNames.OrderBy(n => n),
            policy.TestAttributeNames.OrderBy(n => n));
        Assert.Equal(AttributePolicy.Default.LifecycleAttributeNames.OrderBy(n => n),
            policy.LifecycleAttributeNames.OrderBy(n => n));
    }

    [Fact]
    public void LoadFile_ReplacesAnIncludedSectionWhole()
    {
        var path = WriteTemp("""
            {
              "testAttributes": { "names": ["MyOwnTestAttribute"] }
            }
            """);

        var policy = AttributePolicy.LoadFile(path);

        // Replacement, not union: the override said exactly what a test is.
        Assert.Equal(new[] { "MyOwnTestAttribute" }, policy.TestAttributeNames);
    }

    [Fact]
    public void LoadFile_ParsesRegistrationDeclarations()
    {
        var path = WriteTemp("""
            {
              "registrations": {
                "declarations": [
                  {
                    "attribute": "ImageSelectionTools.Attributes.RegisterDependencyAttribute<TInterface>",
                    "serviceTypeFrom": "typeArgument:0",
                    "lifetimeFrom": "named:Lifetime"
                  }
                ]
              }
            }
            """);

        var declaration = Assert.Single(AttributePolicy.LoadFile(path).Registrations);

        Assert.Equal("ImageSelectionTools.Attributes.RegisterDependencyAttribute<TInterface>", declaration.Attribute);
        Assert.Equal("typeArgument:0", declaration.ServiceTypeFrom);
        Assert.Equal("named:Lifetime", declaration.LifetimeFrom);
    }

    private static string WriteTemp(string json)
    {
        var path = Path.Combine(Path.GetTempPath(), $"attribute-policy-test-{Guid.NewGuid():N}.json");
        File.WriteAllText(path, json);
        return path;
    }
}
