using SampleLib;

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddRazorPages();
builder.Services.AddScoped<ICatalogStore, CatalogStore>();

var app = builder.Build();
app.MapRazorPages();
app.Run();
