namespace SampleLib;

/// <summary>
/// Deliberately christmas-tree shaped: foreach > if > for > if, depth 4, with
/// the only call site at the bottom. Fixture for bodyDepth stamping and for
/// body-graph guard-depth extraction. Do not flatten — the nesting is the point.
/// </summary>
public class NestedFlow
{
    public int Tally(IEnumerable<int> values)
    {
        var total = 0;
        foreach (var value in values)
        {
            if (value >= 0)
            {
                for (var i = 0; i < value; i++)
                {
                    if (i % 2 == 0)
                    {
                        total += CatalogStore.Seed();
                    }
                }
            }
        }
        return total;
    }

    /// <summary>Flat contrast case: no nesting, bodyDepth must be absent.</summary>
    public int Zero() => 0;
}
