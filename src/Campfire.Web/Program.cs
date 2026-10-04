var builder = WebApplication.CreateBuilder(args);
var app = builder.Build();

app.MapGet("/up", () => Results.Text("ok", "text/plain"));

app.Run();

public partial class Program;
