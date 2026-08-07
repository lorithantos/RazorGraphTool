namespace SampleLib;

/// <summary>
/// Fixture for exception-escape analysis. Every scenario the escape sweep must
/// distinguish lives here: unguarded, guarded-by-base, mis-guarded, filtered,
/// rethrowing, wrapping, and fully self-handled. Nothing else in the fixtures
/// calls into this class — coverage and depth assertions elsewhere depend on
/// that isolation. Do not "fix" the deliberate mistakes; they are the fixture.
/// </summary>
public class CustomException : InvalidOperationException
{
    public CustomException(string message) : base(message) { }
}

public class Throwing
{
    /// <summary>The canonical escaping seed: throws, handles nothing.</summary>
    public static int UnguardedThrow()
    {
        throw new CustomException("unguarded");
    }

    /// <summary>Caught by a base type — must NOT escape (assignability positive).</summary>
    public static int GuardedCaller()
    {
        try
        {
            return UnguardedThrow();
        }
        catch (InvalidOperationException)
        {
            return -1;
        }
    }

    /// <summary>Catch of an unrelated type — must escape (assignability negative).</summary>
    public static int MisguardedCaller()
    {
        try
        {
            return UnguardedThrow();
        }
        catch (ArgumentException)
        {
            return -1;
        }
    }

    /// <summary>Catch with a when filter — escapes as conditionally handled.</summary>
    public static int FilteredCaller()
    {
        try
        {
            return UnguardedThrow();
        }
        catch (CustomException e) when (e.Message.Length > 0)
        {
            return -1;
        }
    }

    /// <summary>Bare rethrow: caught, then thrown on. Escapes (conservatively as the catch's type).</summary>
    public static int RethrowingCaller()
    {
        try
        {
            return UnguardedThrow();
        }
        catch
        {
            throw;
        }
    }

    /// <summary>
    /// Wrap-and-throw: a downstream catch of CustomException must not stop the
    /// ApplicationException that actually leaves here.
    /// </summary>
    public static int WrappingCaller()
    {
        try
        {
            return UnguardedThrow();
        }
        catch (CustomException e)
        {
            throw new ApplicationException("wrap", e);
        }
    }

    /// <summary>Throw fully handled in the same method — contributes nothing to escapes.</summary>
    public static int SafeThrow()
    {
        try
        {
            throw new CustomException("contained");
        }
        catch (CustomException)
        {
            return 0;
        }
    }
}

/// <summary>
/// The extension-method fixture: callers use the reduced form (host.DoubleOrThrow()),
/// which drops the this parameter from the bound symbol — the id-mismatch case
/// that silently severed every call edge into an extension method. The escape
/// chain through it is the regression proof.
/// </summary>
public static class ThrowingExtensions
{
    public static int DoubleOrThrow(this Throwing host)
        => Throwing.UnguardedThrow() * 2;
}
