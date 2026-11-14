using AulaIA.Offline;
using Scalar.AspNetCore;

var builder = WebApplication.CreateBuilder(args);

builder.WebHost.UseUrls("http://localhost:5066");

builder.Services.AddOpenApi();
builder.Services.AddCors(options =>
    options.AddDefaultPolicy(policy =>
        policy.AllowAnyOrigin().AllowAnyHeader().AllowAnyMethod()));

builder.Services.AddProblemDetails();
builder.Services.AddInfra(builder.Configuration);

builder.Services.AddTransient<ConhecimentoMcpClient>();
builder.Services.AddTransient<AtendimentoService>();

var app = builder.Build();

app.UseCors();
app.UseExceptionHandler();

app.MapGet("/health", () => Results.Ok(new
{
    servico = "Api.Atendimento.Offline (Ollama)",
    status = "online"
}));

app.MapPost(
    "/api/atendimentos",
    async (
        PerguntaRequest request,
        AtendimentoService service,
        CancellationToken ct) =>
    {
        if (string.IsNullOrWhiteSpace(request.Pergunta)
            || request.Pergunta.Length > 2000)
        {
            return Results.BadRequest(new
            {
                mensagem =
                    "A pergunta deve conter de 1 a 2000 caracteres."
            });
        }

        var resultado = await service.ExecutarAsync(
            request.Pergunta.Trim(),
            ct);

        return Results.Ok(resultado);
    });

app.MapOpenApi();

app.MapScalarApiReference(options => options
    .WithTheme(ScalarTheme.BluePlanet));

app.Run();
