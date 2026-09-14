namespace SampleLib;

/// <summary>
/// Member-extraction fixture: properties, fields, and statics as graph nodes,
/// reads and writes attributed to the code that performs them, and declared
/// types as References edges. Each member exists for one assertion.
/// </summary>
public partial class PriceBook
{
    /// <summary>Written by the ctor, read by Total — the DI-field idiom.</summary>
    private readonly ICatalogStore _store;

    /// <summary>Static mutable state; ++ in Total must yield both a read and a write.</summary>
    public static int Lookups;

    /// <summary>A const is a field node carrying isConst (and isStatic — consts are).</summary>
    public const string DefaultRegion = "EU";

    /// <summary>
    /// Static property. Its initializer reads DefaultRegion, but static
    /// initializers run in the static ctor, which is not a node — that access
    /// must NOT surface as an edge from anywhere.
    /// </summary>
    public static string Region { get; set; } = DefaultRegion;

    /// <summary>Computed property reading another property — the prop→prop Reads case.</summary>
    public decimal Markup => BasePrice * 2;

    /// <summary>Declared type is an in-solution record — the who-uses-this-type case.</summary>
    public PriceTag? Current { get; private set; }

    public decimal BasePrice { get; set; }

    public PriceBook(ICatalogStore store)
    {
        _store = store;
    }

    public decimal Total()
    {
        Lookups++;
        Current = new PriceTag(Region, BasePrice);
        History.Add(Current);
        return BasePrice + _store.List().Count;
    }
}

/// <summary>
/// The second half of the partial: mimics an MVVM-toolkit-style generator
/// split. Each declaration yields its own SymbolInfo carrying the FULL member
/// list, so member nodes and References edges must not double.
/// </summary>
public partial class PriceBook
{
    /// <summary>List-wrapped declared type must still reference the element type.</summary>
    public List<PriceTag> History { get; } = new();
}

/// <summary>
/// Positional record: Label and Amount must become Property nodes; the
/// compiler's EqualityContract must not.
/// </summary>
public record PriceTag(string Label, decimal Amount);

/// <summary>
/// Record with an initializer and no explicit ctor. Its implicit parameterless
/// ctor is a node because the initializer is the code it runs; its synthesized
/// copy ctor is NOT — Roslyn's copy ctor copies fields and never runs an
/// initializer, and it has no syntax, so a row for it would have no line.
/// </summary>
public record PriceQuote
{
    public string Currency { get; init; } = "EUR";
}

/// <summary>
/// Record whose copy ctor is written by hand: the same one-parameter-of-own-type
/// shape as the synthesized one, but authored code with a body and a line, so
/// it must remain a node.
/// </summary>
public record SignedPriceQuote
{
    public string Signer { get; init; } = "";

    public SignedPriceQuote() { }

    protected SignedPriceQuote(SignedPriceQuote other)
    {
        Signer = other.Signer.Trim();
    }
}
