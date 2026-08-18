using Microsoft.AspNetCore.Mvc;
using SampleApp.Services;

namespace SampleApp.Api;

[ApiController]
[Route("api/greetings")]
public class GreetingsController : ControllerBase
{
    private readonly IGreetingService _greetings;

    public GreetingsController(IGreetingService greetings) => _greetings = greetings;

    [HttpGet("{name}")]
    public IActionResult Get([FromRoute] string name) => Ok(_greetings.Greet(name));
}
