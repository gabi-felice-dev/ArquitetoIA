using System.Net.Http.Headers;
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

        // Cliente nomeado "OpenAI" (provedor principal).
        services.AddHttpClient(LlmGateway.OpenAi, http =>
        {
            var apiKey = configuration["OpenAI:ApiKey"];

            if (string.IsNullOrWhiteSpace(apiKey))
                throw new InvalidOperationException(
                    "OpenAI:ApiKey não configurada.");

            http.BaseAddress = new Uri("https://api.openai.com/v1/");

            // Timeout curto: se a OpenAI travar, o fallback entra logo.
            http.Timeout = TimeSpan.FromSeconds(
                configuration.GetValue("OpenAI:TimeoutSegundos", 30));

            http.DefaultRequestHeaders.Authorization =
                new AuthenticationHeaderValue("Bearer", apiKey);
        });

        // Cliente nomeado "Gemini" (fallback) via endpoint compatível OpenAI.
        services.AddHttpClient(LlmGateway.Gemini, http =>
        {
            var apiKey = configuration["Gemini:ApiKey"];

            if (string.IsNullOrWhiteSpace(apiKey))
                throw new InvalidOperationException(
                    "Gemini:ApiKey não configurada.");

            http.BaseAddress = new Uri(
                "https://generativelanguage.googleapis.com/v1beta/openai/");

            http.Timeout = TimeSpan.FromSeconds(
                configuration.GetValue("Gemini:TimeoutSegundos", 60));

            http.DefaultRequestHeaders.Authorization =
                new AuthenticationHeaderValue("Bearer", apiKey);
        });

        services.AddSingleton<LlmGateway>();
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