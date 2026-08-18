namespace RazorGraph.Extractor.Attributes;

using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;

/// <summary>
/// Every name-set decision the extractor makes about attributes, as data: which
/// attribute names mean test, lifecycle hook, controller marker, bound
/// property; whose argument payloads to withhold; which attributes declare a
/// container registration. The shipped default is embedded beside this class
/// (attribute-policy.json); a build overrides it from disk, which is what makes
/// the policy configuration rather than code — changing it must never require a
/// rebuild. Modeled on ExternalApiCatalog.LoadEmbedded in RazorGraph.Lua.
/// </summary>
public sealed class AttributePolicy
{
    /// <summary>The embedded shipped default — the policy every build uses unless told otherwise.</summary>
    public static AttributePolicy Default { get; } = LoadEmbedded();

    /// <summary>Simple names marking a method as a test (FactAttribute, ...).</summary>
    public IReadOnlySet<string> TestAttributeNames { get; }

    /// <summary>Simple names marking a setup/teardown hook.</summary>
    public IReadOnlySet<string> LifecycleAttributeNames { get; }

    /// <summary>Simple names marking a type as a controller without a Controller base.</summary>
    public IReadOnlySet<string> ControllerAttributeNames { get; }

    /// <summary>Simple names marking a property as request-bound.</summary>
    public IReadOnlySet<string> BindPropertyAttributeNames { get; }

    /// <summary>
    /// FULL attribute names whose DecoratedBy edges carry no args/named/source
    /// payload. The edge itself, its line, and unresolvedArgs are not
    /// suppressible — a build error must never be configurable into silence.
    /// </summary>
    public IReadOnlySet<string> SuppressArgumentsFor { get; }

    /// <summary>
    /// Per-codebase registration vocabulary — attributes that mean "this type is
    /// registered with a container". Carried as data for the DI lookup work;
    /// nothing in the extractor consumes it yet.
    /// </summary>
    public IReadOnlyList<RegistrationDeclaration> Registrations { get; }

    private AttributePolicy(PolicyDto dto, AttributePolicy? defaults)
    {
        TestAttributeNames = Section("testAttributes", dto.TestAttributes, defaults?.TestAttributeNames);
        LifecycleAttributeNames = Section("lifecycleAttributes", dto.LifecycleAttributes, defaults?.LifecycleAttributeNames);
        ControllerAttributeNames = Section("controllerAttributes", dto.ControllerAttributes, defaults?.ControllerAttributeNames);
        BindPropertyAttributeNames = Section("bindPropertyAttributes", dto.BindPropertyAttributes, defaults?.BindPropertyAttributeNames);
        SuppressArgumentsFor = Section("suppressArgumentsFor", dto.SuppressArgumentsFor, defaults?.SuppressArgumentsFor);

        Registrations = dto.Registrations?.Declarations
                ?.Select(d => new RegistrationDeclaration(
                    d.Attribute ?? throw new InvalidOperationException(
                        "A registrations declaration is missing its 'attribute' name."),
                    d.ServiceTypeFrom ?? throw new InvalidOperationException(
                        $"Registration for {d.Attribute} is missing 'serviceTypeFrom'."),
                    d.LifetimeFrom))
                .ToList()
            ?? defaults?.Registrations
            ?? throw MissingSection("registrations");
    }

    /// <summary>
    /// A section an override file omits is inherited from the default whole; a
    /// section it includes replaces the default whole. The embedded default has
    /// no fallback, so there every section is required — a hole in the shipped
    /// file is a packaging defect, and it must fail the first load, not
    /// silently classify nothing.
    /// </summary>
    private static IReadOnlySet<string> Section(string name, NameSetDto? section, IReadOnlySet<string>? fallback)
    {
        if (section?.Names is { } names) return new HashSet<string>(names, StringComparer.Ordinal);
        return fallback ?? throw MissingSection(name);
    }

    private static InvalidOperationException MissingSection(string name) =>
        new($"The embedded attribute policy is missing its '{name}' section — the shipped default must be complete.");

    /// <summary>
    /// Load an override from disk. Sections the file omits are inherited from
    /// the embedded default, so a one-line override stays one line.
    /// </summary>
    public static AttributePolicy LoadFile(string path)
    {
        using var stream = File.OpenRead(path);
        var dto = JsonSerializer.Deserialize<PolicyDto>(stream, Options)
            ?? throw new InvalidOperationException($"Attribute policy at {path} deserialized to nothing.");
        return new AttributePolicy(dto, Default);
    }

    private static AttributePolicy LoadEmbedded()
    {
        const string resource = "RazorGraph.Extractor.Attributes.attribute-policy.json";
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(resource)
            ?? throw new InvalidOperationException($"Embedded attribute policy not found: {resource}");
        var dto = JsonSerializer.Deserialize<PolicyDto>(stream, Options)
            ?? throw new InvalidOperationException("Embedded attribute policy failed to deserialize.");
        return new AttributePolicy(dto, defaults: null);
    }

    // Strict JSON with ".comment" members for prose, per house convention; the
    // comments are simply unmapped properties and skipped.
    private static readonly JsonSerializerOptions Options = new()
    {
        ReadCommentHandling = JsonCommentHandling.Disallow
    };

    private sealed class PolicyDto
    {
        [JsonPropertyName("testAttributes")] public NameSetDto? TestAttributes { get; set; }
        [JsonPropertyName("lifecycleAttributes")] public NameSetDto? LifecycleAttributes { get; set; }
        [JsonPropertyName("controllerAttributes")] public NameSetDto? ControllerAttributes { get; set; }
        [JsonPropertyName("bindPropertyAttributes")] public NameSetDto? BindPropertyAttributes { get; set; }
        [JsonPropertyName("suppressArgumentsFor")] public NameSetDto? SuppressArgumentsFor { get; set; }
        [JsonPropertyName("registrations")] public RegistrationsDto? Registrations { get; set; }
    }

    private sealed class NameSetDto
    {
        [JsonPropertyName("names")] public List<string>? Names { get; set; }
    }

    private sealed class RegistrationsDto
    {
        [JsonPropertyName("declarations")] public List<RegistrationDto>? Declarations { get; set; }
    }

    private sealed class RegistrationDto
    {
        [JsonPropertyName("attribute")] public string? Attribute { get; set; }
        [JsonPropertyName("serviceTypeFrom")] public string? ServiceTypeFrom { get; set; }
        [JsonPropertyName("lifetimeFrom")] public string? LifetimeFrom { get; set; }
    }
}

/// <summary>
/// One registration-attribute declaration: which attribute, where the service
/// type is written ("typeArgument:0", "argument:0", "named:ServiceType"), and
/// optionally where the lifetime is ("named:Lifetime"). Free-form selectors,
/// validated by the consumer when one exists.
/// </summary>
public sealed record RegistrationDeclaration(string Attribute, string ServiceTypeFrom, string? LifetimeFrom);
