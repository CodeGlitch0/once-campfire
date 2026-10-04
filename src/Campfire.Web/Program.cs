using Campfire.Web.Data;
using Campfire.Web.FirstRuns;
using Campfire.Web.Http;
using Campfire.Web.Sessions;

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddSingleton<CampfireDatabase>();
builder.Services.AddSingleton<LoginRateLimit>();
var app = builder.Build();

app.MapGet("/up", () => Results.Text("ok", "text/plain"));
CampfirePipeline.Use(app);
FirstRunEndpoints.Map(app);
SessionEndpoints.Map(app);

app.Run();

public partial class Program;
