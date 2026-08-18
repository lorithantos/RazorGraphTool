namespace SampleApp.Services;

using SampleApp.Infrastructure;

public interface IGreetingService
{
    string Greet(string name);
}

[RegisterService<IGreetingService>]
public class GreetingService : IGreetingService
{
    public string Greet(string name) => $"Hello, {name}!";
}
