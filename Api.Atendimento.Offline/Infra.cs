using System.Net.Http.Json;
using System.Text.Json;
using Npgsql;

namespace AulaIA.Offline;

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

        services.AddHttpClient<OllamaGateway>(http =>
        {
            var baseUrl = configuration["Ollama:BaseUrl"]
                ?? "http://localhost:11434/v1/";

            http.BaseAddress = new Uri(baseUrl);
            http.Timeout = TimeSpan.FromMinutes(3);
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
        int saida)
    {
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
                0, 0, 0, 0
            )
            """);

        cmd.Parameters.AddWithValue("id", Guid.NewGuid());
        cmd.Parameters.AddWithValue("execucao", execucaoId);
        cmd.Parameters.AddWithValue("operacao", operacao);
        cmd.Parameters.AddWithValue("modelo", modelo);
        cmd.Parameters.AddWithValue("entrada", entrada);
        cmd.Parameters.AddWithValue("cache", cache);
        cmd.Parameters.AddWithValue("saida", saida);

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

public sealed class OllamaGateway(
    HttpClient http,
    IConfiguration configuration,
    ConsumoRepository consumos)
{
    public string EmbeddingModel =>
        configuration["Ollama:EmbeddingModel"] ?? "nomic-embed-text";

    public string ChatModel =>
        configuration["Ollama:ChatModel"] ?? "qwen3:4b-instruct";

    public async Task<JsonElement> PostAsync(
        string endpoint,
        object payload,
        Guid execucaoId,
        string operacao,
        bool embedding,
        CancellationToken ct)
    {
        using var response = await http.PostAsJsonAsync(endpoint, payload, ct);

        var body = await response.Content.ReadAsStringAsync(ct);

        if (!response.IsSuccessStatusCode)
        {
            Console.WriteLine($"Endpoint: {endpoint}");
            Console.WriteLine($"Status: {(int)response.StatusCode}");
            Console.WriteLine($"Erro Ollama: {body}");

            throw new HttpRequestException(
                $"Ollama retornou HTTP {(int)response.StatusCode}. " +
                $"Detalhes: {body}",
                null,
                response.StatusCode);
        }

        using var document = JsonDocument.Parse(body);

        var root = document.RootElement;

        var entrada = 0;
        var saida = 0;
        var cache = 0;

        if (root.TryGetProperty("usage", out var usage)
            && usage.ValueKind == JsonValueKind.Object)
        {
            if (usage.TryGetProperty("prompt_tokens", out var pt))
                entrada = pt.GetInt32();

            if (!embedding && usage.TryGetProperty("completion_tokens", out var ctTokens))
                saida = ctTokens.GetInt32();

            if (!embedding
                && usage.TryGetProperty("prompt_tokens_details", out var details)
                && details.ValueKind == JsonValueKind.Object
                && details.TryGetProperty("cached_tokens", out var cached))
            {
                cache = cached.GetInt32();
            }
        }

        var modelo = root.TryGetProperty("model", out var modelProp)
            ? modelProp.GetString() ?? (embedding ? EmbeddingModel : ChatModel)
            : (embedding ? EmbeddingModel : ChatModel);

        // 100% gratuito com Ollama local: registra tokens consumidos com custo 0 USD.
        await consumos.RegistrarAsync(
            execucaoId,
            operacao,
            modelo,
            entrada,
            cache,
            saida);

        return root.Clone();
    }
}
