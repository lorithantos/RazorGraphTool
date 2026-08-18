namespace SampleApp.Models;

using System.ComponentModel;
using Microsoft.AspNetCore.Mvc;
using SampleApp.Infrastructure;

public class IndexViewModel
{
    public string Title { get; set; } = string.Empty;
    public int VisitCount { get; set; }

    // typeof naming a type the solution declares: the framework constructs the
    // binder with no call site anywhere, which is what Registers exists to show.
    [ModelBinder(typeof(GreetingNameBinder))]
    public string? BoundName { get; set; }

    // typeof naming a type outside the solution: no Registers edge, the fact
    // rides the DecoratedBy payload instead.
    [TypeConverter(typeof(StringConverter))]
    public string? ConvertedTitle { get; set; }
}
