using AulaIA.Offline;
using ModelContextProtocol.Server;
using Scalar.AspNetCore;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddOpenApi();
builder.Services.AddCors(options =>
    options.AddDefaultPolicy(policy =>
        policy.AllowAnyOrigin().AllowAnyHeader().AllowAnyMethod()));

builder.WebHost.UseUrls("http://localhost:5134");

builder.Services.AddProblemDetails();
builder.Services.AddInfra(builder.Configuration);

builder.Services.AddTransient<ConhecimentoService>();

builder.Services
    .AddMcpServer()
    .WithHttpTransport()
    .WithTools<ConhecimentoTools>();

var app = builder.Build();

await app.EnsureDatabaseAsync();

app.UseCors();
app.UseExceptionHandler();

app.MapGet("/health", () => Results.Ok(new
{
    servico = "Api.Conhecimento.Offline (Ollama)",
    status = "online"
}));

app.MapPost(
    "/api/conhecimentos",
    async (
        IngestRequest request,
        ConhecimentoService service,
        CancellationToken ct) =>
    {
        if (string.IsNullOrWhiteSpace(request.Titulo)
            || request.Titulo.Length > 200
            || string.IsNullOrWhiteSpace(request.Fonte)
            || request.Fonte.Length > 500
            || string.IsNullOrWhiteSpace(request.Texto)
            || request.Texto.Length > 20000)
        {
            return Results.BadRequest(new
            {
                mensagem =
                    "Informe título até 200 caracteres, fonte até 500 " +
                    "e texto entre 1 e 20000 caracteres."
            });
        }

        var resultado = await service.IngerirAsync(request, ct);

        return Results.Ok(resultado);
    });

app.MapMcp("/mcp");

app.MapOpenApi();

app.MapScalarApiReference(options => options
    .WithTheme(ScalarTheme.BluePlanet));

app.Run();
