namespace SampleLib;

/// <summary>
/// A partial type whose members are declared out of name order and split across two
/// files, the shape CommunityToolkit's generated partials give a view model. The
/// enumeration order of partial declarations is not fixed, so the node's properties
/// and methods lists are sorted by name rather than left in whatever order Roslyn
/// handed them over -- otherwise two builds of one source compare unequal.
/// </summary>
public partial class SortedMembers
{
    public string Zeta { get; set; } = "";

    public int Alpha { get; set; }

    public void Run()
    {
    }

    public void Close()
    {
    }
}
