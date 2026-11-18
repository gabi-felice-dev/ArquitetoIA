using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace AulaIA;

public sealed record ResultadoLlm(JsonElement Raiz, string Provedor);

// Lançada quando OpenAI e Gemini falham na mesma execução.
public sealed class ProvedoresIndisponiveisException(Exception inner)
    : Exception("Nenhum provedor de IA disponível no momento.", inner);

// Gateway com fallback: tenta OpenAI; se falhar, usa Gemini
// pelo endpoint compatível com a API da OpenAI.
public sealed class LlmGateway(
    IHttpClientFactory httpFactory,
    IConfiguration configuration,
    ConsumoRepository consumos,
    ILogger<LlmGateway> logger)
{
    public const string OpenAi = "OpenAI";
    public const string Gemini = "Gemini";

    public async Task<ResultadoLlm> ChatAsync(
        IReadOnlyList<MensagemChat> mensagens,
        JsonElement schema,
        Guid execucaoId,
        CancellationToken ct)
    {
        try
        {
            // Chave para demonstrar o fallback em sala sem derrubar a internet.
            if (configuration.GetValue<bool>("Fallback:SimularFalhaOpenAi"))
            {
                throw new HttpRequestException(
                    "Falha simulada da OpenAI.",
                    null,
                    HttpStatusCode.ServiceUnavailable);
            }

            return await EnviarAsync(OpenAi, mensagens, schema, execucaoId, ct);
        }
        catch (Exception ex) when (DeveUsarFallback(ex, ct))
        {
            logger.LogWarning(
                "OpenAI falhou na execução {ExecucaoId} ({Erro}). " +
                "Acionando fallback para Gemini.",
                execucaoId,
                ex.Message);

            try
            {
                return await EnviarAsync(Gemini, mensagens, schema, execucaoId, ct);
            }
            catch (Exception exGemini) when (!ct.IsCancellationRequested)
            {
                // Free tier do Gemini tem limite baixo (429). Se os dois
                // caírem, o serviço responde com mensagem amigável.
                logger.LogError(
                    "Gemini também falhou na execução {ExecucaoId} ({Erro}).",
                    execucaoId,
                    exGemini.Message);

                throw new ProvedoresIndisponiveisException(exGemini);
            }
        }
    }

    private static bool DeveUsarFallback(Exception ex, CancellationToken ct) =>
        ex switch
        {
            // O usuário desistiu: não gaste com o segundo provedor.
            OperationCanceledException when ct.IsCancellationRequested => false,

            // 400 = payload inválido (bug nosso). O Gemini também falharia.
            HttpRequestException { StatusCode: HttpStatusCode.BadRequest } => false,

            // Rede, timeout, 401/403, 404 (modelo), 408, 429 e 5xx.
            HttpRequestException => true,
            TaskCanceledException => true, // timeout do HttpClient
            JsonException => true,         // resposta malformada
            KeyNotFoundException => true,  // JSON sem os campos esperados
            _ => false
        };

    private async Task<ResultadoLlm> EnviarAsync(
        string provedor,
        IReadOnlyList<MensagemChat> mensagens,
        JsonElement schema,
        Guid execucaoId,
        CancellationToken ct)
    {
        var secao = configuration.GetSection(provedor);
        var modeloConfigurado = secao["ChatModel"]!;
        var http = httpFactory.CreateClient(provedor);

        var responseFormat = new
        {
            type = "json_schema",
            json_schema = new
            {
                name = "resposta_atendimento",
                strict = true,
                schema
            }
        };

        // Cada provedor tem pequenas diferenças de parâmetros.
        object payload = provedor == Gemini
            ? new
            {
                model = modeloConfigurado,
                max_tokens = secao.GetValue("MaxTokens", 2048),
                reasoning_effort = secao["ReasoningEffort"] ?? "low",
                messages = mensagens,
                response_format = responseFormat
            }
            : new
            {
                model = modeloConfigurado,
                max_completion_tokens = secao.GetValue("MaxTokens", 800),
                messages = mensagens,
                response_format = responseFormat
            };

        using var response = await http.PostAsJsonAsync(
            "chat/completions", payload, ct);

        var body = await response.Content.ReadAsStringAsync(ct);

        if (!response.IsSuccessStatusCode)
        {
            throw new HttpRequestException(
                $"{provedor} retornou HTTP {(int)response.StatusCode}: {body}",
                null,
                response.StatusCode);
        }

        using var document = JsonDocument.Parse(body);
        var root = document.RootElement;
        var usage = root.GetProperty("usage");

        var entrada = usage.GetProperty("prompt_tokens").GetInt32();
        var saida = usage.GetProperty("completion_tokens").GetInt32();
        var cache = 0;

        if (usage.TryGetProperty("prompt_tokens_details", out var details)
            && details.ValueKind == JsonValueKind.Object
            && details.TryGetProperty("cached_tokens", out var cached)
            && cached.ValueKind == JsonValueKind.Number)
        {
            cache = cached.GetInt32();
        }

        var modelo =
            root.TryGetProperty("model", out var m)
            && m.ValueKind == JsonValueKind.String
                ? m.GetString()!
                : modeloConfigurado;

        // Auditoria por provedor: o custo do fallback também é registrado
        // (no free tier do Gemini o custo fica 0, mas os tokens aparecem).
        await consumos.RegistrarAsync(
            execucaoId,
            $"resposta_chat_{provedor.ToLowerInvariant()}",
            modelo,
            entrada,
            cache,
            saida,
            secao.GetValue<decimal>("ChatInputPerMillion"),
            secao.GetValue<decimal>("ChatCachedInputPerMillion"),
            secao.GetValue<decimal>("ChatOutputPerMillion"));

        return new ResultadoLlm(root.Clone(), provedor);
    }
}