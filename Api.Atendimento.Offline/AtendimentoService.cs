using System.Text.Json;
using System.Text.Json.Serialization;

namespace AulaIA.Offline;

public sealed record PerguntaRequest(string Pergunta);

public sealed record RespostaIa(
    [property: JsonPropertyName("resposta")]
    string Resposta,

    [property: JsonPropertyName("informacaoEncontrada")]
    bool InformacaoEncontrada,

    [property: JsonPropertyName("encaminharParaHumano")]
    bool EncaminharParaHumano,

    [property: JsonPropertyName("fontes")]
    string[] Fontes);

public sealed record FonteResposta(
    Guid ChunkId,
    string Titulo,
    string Fonte);

public sealed record AtendimentoResponse(
    Guid ExecucaoId,
    string Resposta,
    bool InformacaoEncontrada,
    bool EncaminharParaHumano,
    List<FonteResposta> Fontes,
    decimal CustoEstimadoUsd);

public sealed class AtendimentoService(
    ConhecimentoMcpClient conhecimento,
    OllamaGateway ollama,
    ConsumoRepository consumos)
{
    private const string SystemPrompt = """
        Você é o assistente virtual da COTI Informática.
        Responda em português do Brasil de forma curta e objetiva usando APENAS o contexto fornecido.
        Retorne EXCLUSIVAMENTE um objeto JSON válido no formato:
        {
          "resposta": "texto para o aluno",
          "informacaoEncontrada": true,
          "encaminharParaHumano": false,
          "fontes": ["id-do-chunk"]
        }
        Se a resposta não estiver no contexto, defina informacaoEncontrada=false, encaminharParaHumano=true e fontes=[].
        """;

    public async Task<AtendimentoResponse> ExecutarAsync(
        string pergunta,
        CancellationToken ct)
    {
        var execucaoId = Guid.NewGuid();
        var swTotal = System.Diagnostics.Stopwatch.StartNew();

        Console.WriteLine($"[Atendimento.Offline] [{execucaoId}] 1/3 Consultando MCP (embedding + pgvector)...");
        var swMcp = System.Diagnostics.Stopwatch.StartNew();

        // 1. Recuperação obrigatória via MCP.
        var trechos = await conhecimento.BuscarAsync(
            pergunta,
            execucaoId,
            ct);

        swMcp.Stop();
        Console.WriteLine($"[Atendimento.Offline] [{execucaoId}] MCP concluído em {swMcp.ElapsedMilliseconds} ms ({trechos.Count} trecho(s) encontrado(s)).");

        if (trechos.Count == 0)
        {
            return await MontarRespostaAsync(
                execucaoId,
                new RespostaIa(
                    "Não encontrei informações suficientes na base da " +
                    "COTI Informática para responder à sua pergunta. " +
                    "Para confirmar essa informação, entre em contato " +
                    "com a nossa equipe de atendimento.",
                    false,
                    true,
                    []),
                trechos,
                ct);
        }

        var contextoTexto = string.Join(
            "\n---\n",
            trechos.Select(t => $"ID: {t.Id}\nTítulo: {t.Titulo}\nConteúdo: {t.Conteudo}"));

        var entradaUsuario = $"Contexto:\n{contextoTexto}\n\nPergunta: {pergunta}";

        var payload = new
        {
            model = ollama.ChatModel,
            temperature = 0.1,
            max_tokens = 300,
            messages = new[]
            {
                new
                {
                    role = "system",
                    content = SystemPrompt
                },
                new
                {
                    role = "user",
                    content = entradaUsuario
                }
            },
            response_format = new
            {
                type = "json_object"
            }
        };

        Console.WriteLine($"[Atendimento.Offline] [{execucaoId}] 2/3 Gerando resposta no Ollama ({ollama.ChatModel})...");
        var swLlm = System.Diagnostics.Stopwatch.StartNew();

        // 2. Geração via Ollama local (100% gratuito).
        var response = await ollama.PostAsync(
            "chat/completions",
            payload,
            execucaoId,
            "resposta_chat_offline",
            embedding: false,
            ct: ct);

        swLlm.Stop();
        Console.WriteLine($"[Atendimento.Offline] [{execucaoId}] Ollama respondeu em {swLlm.ElapsedMilliseconds} ms (Total: {swTotal.ElapsedMilliseconds} ms).");

        var choice = response.GetProperty("choices")[0];
        var message = choice.GetProperty("message");

        var json = message.GetProperty("content").GetString();

        var resposta = JsonSerializer.Deserialize<RespostaIa>(
            json ?? "")
            ?? throw new InvalidOperationException(
                "Resposta estruturada inválida.");

        if (string.IsNullOrWhiteSpace(resposta.Resposta))
        {
            throw new InvalidOperationException(
                "Campos obrigatórios ausentes na resposta.");
        }

        var idsPermitidos = trechos
            .Select(t => t.Id.ToString())
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        if (!resposta.InformacaoEncontrada)
        {
            resposta = resposta with
            {
                EncaminharParaHumano = true,
                Fontes = []
            };
        }
        else
        {
            var fontesValidas = (resposta.Fontes ?? [])
                .Where(id => idsPermitidos.Contains(id))
                .ToArray();

            // Caso o modelo local 4B encontre a resposta no contexto mas não copie o UUID exato,
            // vincula ao trecho mais relevante recuperado do contexto.
            if (fontesValidas.Length == 0)
            {
                fontesValidas = [trechos[0].Id.ToString()];
            }

            resposta = resposta with
            {
                EncaminharParaHumano = false,
                Fontes = fontesValidas
            };
        }

        return await MontarRespostaAsync(
            execucaoId,
            resposta,
            trechos,
            ct);
    }

    private async Task<AtendimentoResponse> MontarRespostaAsync(
        Guid execucaoId,
        RespostaIa resposta,
        List<Trecho> trechos,
        CancellationToken ct)
    {
        var fontes = trechos
            .Where(t => resposta.Fontes.Contains(t.Id.ToString(), StringComparer.OrdinalIgnoreCase))
            .Select(t => new FonteResposta(
                t.Id,
                t.Titulo,
                t.Fonte))
            .ToList();

        var custo = await consumos.TotalAsync(execucaoId, ct);

        return new AtendimentoResponse(
            execucaoId,
            resposta.Resposta,
            resposta.InformacaoEncontrada,
            resposta.EncaminharParaHumano,
            fontes,
            custo);
    }
}
