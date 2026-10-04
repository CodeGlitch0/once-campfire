using Campfire.Web.Data;
using Campfire.Web.FirstRuns;
using Campfire.Web.Http;
using Campfire.Web.Identity;
using Campfire.Web.Sessions;

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddSingleton<CampfireDatabase>();
builder.Services.AddSingleton<LoginRateLimit>();
var app = builder.Build();

// Outside Development this throws, so the host does not sign with the git constant.
SecretKey.Resolve(app.Configuration, app.Environment);

// Rewrite _method before routing. An explicit UseRouting keeps the automatic
// routing middleware from matching the original POST first.
app.Use(async (context, next) =>
{
    await MethodOverride.ApplyAsync(context.Request);
    await next();
});
app.UseRouting();

app.MapGet("/up", () => Results.Text("ok", "text/plain"));
CampfirePipeline.Use(app);
FirstRunEndpoints.Map(app);
SessionEndpoints.Map(app);
UserEndpoints.Map(app);
AccountEndpoints.Map(app);

app.Run();

public partial class Program;
