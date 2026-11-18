using AulaIA;
using Scalar.AspNetCore;

var builder = WebApplication.CreateBuilder(args);

builder.WebHost.UseUrls("http://localhost:5065");

builder.Services.AddOpenApi();
builder.Services.AddCors(options =>
    options.AddDefaultPolicy(policy =>
        policy.AllowAnyOrigin().AllowAnyHeader().AllowAnyMethod()));
builder.Services.AddProblemDetails();

builder.Services.AddInfra(builder.Configuration);                    // Postgres + OpenAI
builder.Services.AddMemoriaConversacional(builder.Configuration);    // MongoDB
// builder.Services.AddNotificacaoSecretaria(builder.Configuration); // ETAPA 3

builder.Services.AddTransient<ConhecimentoMcpClient>();
builder.Services.AddTransient<AtendimentoService>();

var app = builder.Build();

app.UseCors();
app.UseExceptionHandler();

// Cria os índices do MongoDB (idempotente) ao subir a API.
await app.Services
    .GetRequiredService<MemoriaConversaRepository>()
    .CriarIndicesAsync(CancellationToken.None);

app.MapGet("/health", () => Results.Ok(new
{
    servico = "Api.Atendimento",
    status = "online"
}));

app.MapPost(
    "/api/atendimentos",
    async (
        PerguntaRequest request,
        AtendimentoService service,
        CancellationToken ct) =>
    {
        if (string.IsNullOrWhiteSpace(request.Nome)
            || request.Nome.Trim().Length is < 2 or > 100)
        {
            return Results.BadRequest(new
            {
                mensagem = "Informe o nome com 2 a 100 caracteres."
            });
        }

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
            new Cliente(request.Nome),
            request.Pergunta.Trim(),
            ct);

        return Results.Ok(resultado);
    });

// GET /api/atendimentos/historico?nome=Maria%20Souza
app.MapGet(
    "/api/atendimentos/historico",
    async (
        string nome,
        MemoriaConversaRepository memoria,
        CancellationToken ct) =>
    {
        var mensagens = await memoria.UltimasAsync(new Cliente(nome), ct);

        return Results.Ok(mensagens.Select(m => new
        {
            m.Nome,
            m.Papel,
            m.Conteudo,
            m.Provedor,
            m.CriadoEm
        }));
    });

// DELETE /api/atendimentos/historico?nome=Maria%20Souza
app.MapDelete(
    "/api/atendimentos/historico",
    async (
        string nome,
        MemoriaConversaRepository memoria,
        CancellationToken ct) =>
    {
        await memoria.LimparAsync(new Cliente(nome), ct);
        return Results.NoContent();
    });

app.MapOpenApi();

app.MapScalarApiReference(options => options
    .WithTheme(ScalarTheme.BluePlanet));

app.Run();