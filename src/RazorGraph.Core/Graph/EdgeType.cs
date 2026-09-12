namespace RazorGraph.Core.Graph;

public enum EdgeType
{
    // Structural
    Contains,
    Inherits,
    Implements,

    // Method-level override: from the override to the base or abstract
    // declaration it replaces. The class-dispatch twin of Implements, and
    // deliberately NOT folded into it — "what interfaces does this implement"
    // is a question people ask of Implements, and answering it with overrides
    // mixed in would be a fuller answer that is also a wrong one. Inherits is
    // class-to-class and cannot stand in: a walk that wants to know which body
    // runs needs the member-to-member relation.
    Overrides,

    // Razor rendering
    PageServedBy,
    UsesLayout,
    RendersPartial,
    RendersComponent,
    DefinesSection,
    HasModel,

    // Data flow
    InjectedInto,
    ReturnsView,
    BindsTo,
    Reads,
    Writes,
    Calls,

    // Routing
    MapsToRoute,
    HandlesHttpMethod,

    // ViewData
    ViewDataWrittenBy,
    ViewDataReadBy,

    // Server-rendered element ids reached by a script's literal selectors,
    // carrying the shared ids. Same family as ViewDataReadBy: state crossing
    // the server/client boundary by name, where a rename breaks one side only.
    DomSelectedBy,

    // Cross-layer
    UrlGeneratedBy,

    // Dependencies
    References,
    DependsOn,

    // Testing. Emitted from a test method to production code it exercises,
    // carrying the call depth at which it was reached, so consumers can ask for
    // direct exercise (depth 1) or the full blast radius.
    Covers,

    // Exception flow. Emitted from a throwing method to an application entry
    // point its exception can reach with no catch stopping it, carrying the
    // exception type, the hop depth, one representative path, and whether the
    // only handling en route was a filtered catch (conditional).
    Escapes,

    // Named binding points. Produces runs from the code that names a binding to
    // the name itself; BoundBy from the name to each template or handler that
    // could serve it, carrying rank and the kind of binding. Kept as two edges
    // rather than one code->template edge because resolution is one-to-many and
    // precedence-ordered: with themes and tenants in play, which candidate wins
    // is not statically decidable, so the graph shows all of them and declines
    // to pick.
    Produces,
    BoundBy,

    // Naming by string. Emitted from the code that produces a string to the declared node
    // whose NAME that string matches, carrying the value, the line, and the provenance --
    // nameof, a typed literal, an attribute argument, or an interpolated segment.
    //
    // The provenance is the finding, not decoration: nameof survives a rename and a typed
    // literal does not, so the identical coupling is a fact in one case and a latent break
    // in the other. Only strings that match a declared name are edges at all; the rest are
    // prose, and indexing them would bury the signal under every message in the codebase.
    //
    // This is the general form of what DomSelectedBy and ViewDataReadBy do for one boundary
    // each: state crossing a seam by name, where a rename breaks one side silently.
    Quotes,

    // Naming by value. Emitted from the code that produces a string to the Literal node
    // holding that string, carrying the line and the provenance. One edge per OCCURRENCE,
    // not per pair: a value typed in five places is five edges, and the incoming set of
    // the Literal node is therefore the list of every site that produces it.
    //
    // Deliberately NOT folded into Quotes, which is the same reasoning that kept
    // Overrides out of Implements. Quotes answers "which strings name a declared symbol",
    // and its worth is that a string matching nothing is not an edge — mixing every
    // literal in would make that answer fuller and wrong. Mentions asks the other
    // question, "where does this exact text appear", which needs no declaration to exist
    // and is the one grep was still being used for.
    Mentions,

    // Annotation. Emitted from a decorated node -- type, method, property, field, parameter or
    // project -- to the attribute's type, carrying the arguments as written, the line, and which
    // target the attribute actually hit. One edge per USAGE, not per attribute type: [Theory]
    // with twenty [InlineData] is twenty-one edges, and without the line they would be
    // indistinguishable from one another. The target matters because a record's primary
    // constructor parameter is a parameter and a property at once, and an attribute on it
    // reaches only one of them.
    DecoratedBy,

    // Registration. Emitted from a decorated node to a type an attribute names -- typeof(...)
    // or a generic attribute's type argument -- because the framework will construct or consult
    // that type on account of the annotation, with no call site anywhere.
    //
    // Deliberately NOT folded into References. That edge answers exactly one question today,
    // "which member declarations mention this type", and its value is that an empty incoming
    // set proves nothing declares one. Mixing registrations in would end that proof while every
    // existing traversal silently absorbed the new edges. Kept apart, it also earns its place:
    // a type reachable only this way has no caller and would otherwise read as dead.
    Registers,

    // Extension surface. Emitted from an extension method to the in-solution
    // type it extends: the method is part of that type's working surface even
    // though containment says it lives on a static class elsewhere.
    Extends,

    // Construction. Emitted from the method containing a `new X()` to the type X,
    // carrying the line. The fact that answers "which implementation did the
    // composition root actually pick" — an interface's implementers are the
    // CANDIDATES, and only construction says which one production builds.
    //
    // A new-expression already produced a Calls edge to the constructor METHOD,
    // so this looks redundant and is not. That edge needs a .ctor node at the far
    // end, and a type whose constructor is compiler-supplied has none — so the
    // plainest types, the ones a composition root news up and passes as an
    // interface, had NO incoming edge of any kind and read as dead. The edge
    // lands on the TYPE precisely so the answer does not depend on whether
    // somebody happened to write a constructor out.
    //
    // Deliberately NOT folded into References, for the reason that kept Registers
    // out of it: References answers "which member declarations mention this type",
    // and its worth is that an empty incoming set proves no declaration names one.
    // Construction is not a declaration mentioning a type, and mixing the two
    // would end that proof while every existing traversal absorbed the new edges.
    // Distinct from Registers too — "the framework will consult this type" and
    // "this method builds one" are different claims, and a reachability answer
    // that cannot tell them apart is the one that sent a reader to grep.
    Constructs,

    // Data binding from markup. Emitted from a view file to each member a binding
    // expression could name, carrying the path as written, the line, and how many
    // candidates the name resolved to.
    //
    // CONSERVATIVE IN THE SAME DIRECTION AS INTERFACE DISPATCH, and for the same
    // reason. A binding resolves against the DataContext of whatever element it
    // sits on at RUNTIME — walked up the visual tree by RelativeSource, rebound
    // mid-tree, or supplied by the item a DataTemplate is applied to — so which
    // member it names is not statically decidable. A call to an interface member
    // reaches every in-solution implementation because reachability cannot say
    // which one runs; a binding reaches every candidate member for exactly that
    // reason. Naming one would be a plausible answer that is sometimes wrong,
    // which is worse than a fan-out a reader can see.
    //
    // NOT folded into BindsTo, which already means model binding in the Razor
    // layer and is in the data-flow traversal set: absorbing markup bindings
    // would change what an existing answer means. Not Quotes either, whose worth
    // is that a string matching nothing is not an edge and whose rule is to DROP
    // an ambiguous name -- right for rename safety, wrong here, because dropping
    // is what leaves a bound member reading as unused.
    //
    // candidates is on the edge rather than implied: a binding that resolved to
    // one member is an exact answer, and one that resolved to six is a question.
    // A reader must be able to tell those apart without counting edges.
    DataBinds,

    Unknown
}
