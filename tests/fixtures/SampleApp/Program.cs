using SampleApp.Services;

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddRazorPages();
builder.Services.AddControllers();
builder.Services.AddScoped<IGreetingService, GreetingService>();

var app = builder.Build();
app.MapRazorPages();
app.MapControllers();
app.Run();
