using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using SampleApp.Services;

namespace SampleApp.Pages;

public class IndexModel : PageModel
{
    private readonly IGreetingService _greetings;

    public IndexModel(IGreetingService greetings) => _greetings = greetings;

    [BindProperty]
    public string Name { get; set; } = string.Empty;

    public string? Message { get; private set; }

    public void OnGet(int? id)
    {
        ViewData["Title"] = "Home";
    }

    public IActionResult OnPost()
    {
        Message = _greetings.Greet(Name);
        return Page();
    }
}
