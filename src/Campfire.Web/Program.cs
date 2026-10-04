using Campfire.Web.Data;
using Campfire.Web.FirstRuns;

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddSingleton<CampfireDatabase>();
var app = builder.Build();

app.MapGet("/up", () => Results.Text("ok", "text/plain"));
FirstRunEndpoints.Map(app);

app.Run();

public partial class Program;
