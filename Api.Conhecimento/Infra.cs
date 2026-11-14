using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Npgsql;

namespace AulaIA;

public static class Infra
{
    public static void AddInfra(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        var connectionString =
            configuration.GetConnectionString("Postgres")
            ?? throw new InvalidOperationException(
                "ConnectionStrings:Postgres não configurada.");

        services.AddSingleton(
            _ => NpgsqlDataSource.Create(connectionString));

        services.AddSingleton<ConsumoRepository>();

        services.AddHttpClient<OpenAiGateway>(http =>
        {
            var apiKey = configuration["OpenAI:ApiKey"];

            if (string.IsNullOrWhiteSpace(apiKey))
                throw new InvalidOperationException(
                    "OpenAI:ApiKey não configurada.");

            http.BaseAddress = new Uri("https://api.openai.com/v1/");
            http.Timeout = TimeSpan.FromSeconds(90);

            http.DefaultRequestHeaders.Authorization =
                new AuthenticationHeaderValue("Bearer", apiKey);
        });
    }
}

public sealed class ConsumoRepository(NpgsqlDataSource dataSource)
{
    public async Task RegistrarAsync(
        Guid execucaoId,
        string operacao,
        string modelo,
        int entrada,
        int cache,
        int saida,
        decimal precoEntrada,
        decimal precoCache,
        decimal precoSaida)
    {
        var custo =
            ((entrada - cache) * precoEntrada
             + cache * precoCache
             + saida * precoSaida) / 1_000_000m;

        // A desconexão do usuário não deve cancelar imediatamente
        // a gravação de um consumo já informado pelo provedor.
        using var timeout = new CancellationTokenSource(
            TimeSpan.FromSeconds(10));

        await using var cmd = dataSource.CreateCommand("""
            INSERT INTO auditoria.consumos (
                id, execucao_id, operacao, modelo,
                tokens_entrada, tokens_cache, tokens_saida,
                preco_entrada_milhao, preco_cache_milhao,
                preco_saida_milhao, custo_usd
            )
            VALUES (
                @id, @execucao, @operacao, @modelo,
                @entrada, @cache, @saida,
                @precoEntrada, @precoCache, @precoSaida, @custo
            )
            """);

        cmd.Parameters.AddWithValue("id", Guid.NewGuid());
        cmd.Parameters.AddWithValue("execucao", execucaoId);
        cmd.Parameters.AddWithValue("operacao", operacao);
        cmd.Parameters.AddWithValue("modelo", modelo);
        cmd.Parameters.AddWithValue("entrada", entrada);
        cmd.Parameters.AddWithValue("cache", cache);
        cmd.Parameters.AddWithValue("saida", saida);
        cmd.Parameters.AddWithValue("precoEntrada", precoEntrada);
        cmd.Parameters.AddWithValue("precoCache", precoCache);
        cmd.Parameters.AddWithValue("precoSaida", precoSaida);
        cmd.Parameters.AddWithValue("custo", custo);

        await cmd.ExecuteNonQueryAsync(timeout.Token);
    }

    public async Task<decimal> TotalAsync(
        Guid execucaoId,
        CancellationToken ct)
    {
        await using var cmd = dataSource.CreateCommand("""
            SELECT COALESCE(SUM(custo_usd), 0)
            FROM auditoria.consumos
            WHERE execucao_id = @execucao
            """);

        cmd.Parameters.AddWithValue("execucao", execucaoId);

        return Convert.ToDecimal(await cmd.ExecuteScalarAsync(ct));
    }
}

public sealed class OpenAiGateway(
    HttpClient http,
    IConfiguration configuration,
    ConsumoRepository consumos)
{
    public string EmbeddingModel =>
        configuration["OpenAI:EmbeddingModel"]!;

    public string ChatModel =>
        configuration["OpenAI:ChatModel"]!;

    public async Task<JsonElement> PostAsync(
        string endpoint,
        object payload,
        Guid execucaoId,
        string operacao,
        bool embedding,
        CancellationToken ct)
    {
        using var response =
            await http.PostAsJsonAsync(endpoint, payload, ct);

        if (!response.IsSuccessStatusCode)
        {
            throw new HttpRequestException(
                $"OpenAI retornou HTTP {(int)response.StatusCode}.",
                null,
                response.StatusCode);
        }

        var body = await response.Content.ReadAsStringAsync(ct);

        using var document = JsonDocument.Parse(body);

        var root = document.RootElement;
        var usage = root.GetProperty("usage");

        var entrada = usage.GetProperty("prompt_tokens").GetInt32();

        var saida = embedding
            ? 0
            : usage.GetProperty("completion_tokens").GetInt32();

        var cache = 0;

        if (!embedding
            && usage.TryGetProperty("prompt_tokens_details", out var details)
            && details.ValueKind == JsonValueKind.Object
            && details.TryGetProperty("cached_tokens", out var cached))
        {
            cache = cached.GetInt32();
        }

        var precoEntrada = configuration.GetValue<decimal>(
            embedding
                ? "OpenAI:EmbeddingInputPerMillion"
                : "OpenAI:ChatInputPerMillion");

        var precoCache = embedding
            ? 0m
            : configuration.GetValue<decimal>(
                "OpenAI:ChatCachedInputPerMillion");

        var precoSaida = embedding
            ? 0m
            : configuration.GetValue<decimal>(
                "OpenAI:ChatOutputPerMillion");

        var modelo = root.GetProperty("model").GetString()!;

        // Registrar antes de interpretar o conteúdo gerado:
        // recusas ou respostas truncadas também podem consumir tokens.
        await consumos.RegistrarAsync(
            execucaoId,
            operacao,
            modelo,
            entrada,
            cache,
            saida,
            precoEntrada,
            precoCache,
            precoSaida);

        return root.Clone();
    }
}