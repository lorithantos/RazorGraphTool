namespace SampleLib;

/// <summary>
/// The C# 14 extension-member fixture: members declared inside an
/// <c>extension</c> block rather than as static methods with a <c>this</c>
/// parameter.
///
/// <para>
/// Three facts make this shape worth a fixture of its own. An extension block
/// is an <c>ExtensionBlockDeclarationSyntax</c>, which derives from
/// <c>TypeDeclarationSyntax</c>, so the type walk visits it. Its symbol has an
/// empty name, which is what made the compiler-generated filter drop it and
/// every member with it. And a call site binds to the block's member symbol,
/// not to the lowered static implementation — so a declaration and its callers
/// only meet if both mint the id the same way.
/// </para>
///
/// <para>
/// Nothing here throws or is reachable from an entry point: the exact escape
/// count asserted in ExceptionExtractionIntegrationTests depends on that.
/// </para>
/// </summary>
public sealed class Price
{
    public int Cents { get; init; }
}

/// <summary>
/// Both block shapes: one with a named receiver (instance members) and one
/// with a receiver type only (static members reached through Price itself).
/// </summary>
public static class PriceExtensions
{
    extension(Price price)
    {
        /// <summary>Instance extension property, read as price.IsFree.</summary>
        public bool IsFree => price.Cents == 0;

        /// <summary>Instance extension method, called in the reduced form.</summary>
        public Price Doubled() => new() { Cents = price.Cents * 2 };
    }

    extension(Price)
    {
        /// <summary>Static extension property, reached as Price.Free.</summary>
        public static Price Free => new() { Cents = 0 };

        /// <summary>Static extension method, reached as Price.FromCents(n).</summary>
        public static Price FromCents(int cents) => new() { Cents = cents };
    }

    /// <summary>
    /// The control, in the same static class: a classic extension method. Its
    /// node id is the static two-parameter form, and the point of keying
    /// extension-block ids off the lowered implementation is that these two
    /// syntaxes produce the same id shape.
    /// </summary>
    public static Price Tripled(this Price price) => new() { Cents = price.Cents * 3 };
}

/// <summary>In-project callers, one per member shape.</summary>
public static class PriceCallers
{
    public static bool AskIsFree(Price price) => price.IsFree;

    public static Price AskDoubled(Price price) => price.Doubled();

    public static Price AskFree() => Price.Free;

    public static Price AskFromCents(int cents) => Price.FromCents(cents);

    public static Price AskTripled(Price price) => price.Tripled();
}
