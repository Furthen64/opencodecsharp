using System.Text.Json;
using System.Text.Json.Serialization;
using OpenCode.Server;

var builder = WebApplication.CreateBuilder(args);

builder.Services.ConfigureHttpJsonOptions(options =>
{
    options.SerializerOptions.PropertyNamingPolicy = JsonNamingPolicy.CamelCase;
    options.SerializerOptions.DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull;
    options.SerializerOptions.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase));
});
builder.Services.AddOpenApi();
builder.Services.AddOpenCodeServer(builder.Configuration, builder.Environment);

var app = builder.Build();

app.UseExceptionHandler(exceptionApp => exceptionApp.Run(ServerErrors.WriteAsync));

if (app.Environment.IsDevelopment())
    app.MapOpenApi();

app.MapOpenCodeRoutes();

await app.InitializeOpenCodeAsync();
app.Run();

public partial class Program;
