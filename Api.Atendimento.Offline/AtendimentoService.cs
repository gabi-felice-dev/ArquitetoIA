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
        IDENTIDADE:
        Você é o assistente virtual de atendimento da COTI Informática.
        Seu papel é atender alunos, futuros alunos e clientes com
        educação, clareza, simpatia e profissionalismo.

        OBJETIVO:
        Ajudar o usuário a esclarecer dúvidas sobre a COTI Informática,
        seus cursos e serviços, usando exclusivamente os fatos
        sustentados pelos trechos recuperados da base de conhecimento.

        REGRAS SOBRE A BASE DE CONHECIMENTO:
        1. Utilize exclusivamente os fatos sustentados pelos trechos
           recuperados. Não complete informações com conhecimento
           externo, suposições ou lembranças.
        2. Responda sempre em português do Brasil.
        3. Se o contexto sustentar suficientemente a resposta:
           - informacaoEncontrada = true
           - encaminharParaHumano = false
           - fontes deve conter o valor exato do campo "id" dos trechos usados.
        4. Se o contexto não sustentar suficientemente a resposta:
           - informacaoEncontrada = false
           - encaminharParaHumano = true
           - fontes = []
        5. Retorne exclusivamente o objeto JSON definido pelo schema.
        """;

    private const string Schema = """
        {
          "type": "object",
          "properties": {
            "resposta": {
              "type": "string"
            },
            "informacaoEncontrada": {
              "type": "boolean"
            },
            "encaminharParaHumano": {
              "type": "boolean"
            },
            "fontes": {
              "type": "array",
              "items": {
                "type": "string"
              }
            }
          },
          "required": [
            "resposta",
            "informacaoEncontrada",
            "encaminharParaHumano",
            "fontes"
          ],
          "additionalProperties": false
        }
        """;

    public async Task<AtendimentoResponse> ExecutarAsync(
        string pergunta,
        CancellationToken ct)
    {
        var execucaoId = Guid.NewGuid();

        // 1. Recuperação obrigatória via MCP.
        var trechos = await conhecimento.BuscarAsync(
            pergunta,
            execucaoId,
            ct);

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

        var contexto = JsonSerializer.Serialize(
            trechos.Select(t => new
            {
                id = t.Id.ToString(),
                titulo = t.Titulo,
                fonte = t.Fonte,
                conteudo = t.Conteudo
            }));

        var entradaUsuario = JsonSerializer.Serialize(new
        {
            pergunta,
            contextoRecuperado = JsonSerializer.Deserialize<JsonElement>(
                contexto)
        });

        using var schemaDocument = JsonDocument.Parse(Schema);

        var payload = new
        {
            model = ollama.ChatModel,
            max_completion_tokens = 800,
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
                type = "json_schema",
                json_schema = new
                {
                    name = "resposta_atendimento",
                    strict = true,
                    schema = schemaDocument.RootElement.Clone()
                }
            }
        };

        // 2. Geração via Ollama local (100% gratuito).
        var response = await ollama.PostAsync(
            "chat/completions",
            payload,
            execucaoId,
            "resposta_chat_offline",
            embedding: false,
            ct: ct);

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
