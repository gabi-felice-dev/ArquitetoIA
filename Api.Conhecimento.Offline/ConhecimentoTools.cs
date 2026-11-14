using System.ComponentModel;
using System.Text.Json;
using ModelContextProtocol.Server;

namespace AulaIA.Offline;

[McpServerToolType]
public sealed class ConhecimentoTools(
    ConhecimentoService conhecimento)
{
    [McpServerTool(Name = "buscar_conhecimento")]
    [Description(
        "Busca trechos relevantes na base corporativa offline (Ollama). " +
        "Retorna JSON com IDs, títulos, fontes, conteúdo e similaridade.")]
    public async Task<string> BuscarConhecimentoAsync(
        [Description("Pergunta a consultar na base.")]
        string pergunta,

        [Description("Identificador UUID da execução para auditoria.")]
        string execucaoId,

        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(pergunta)
            || pergunta.Length > 2000)
        {
            throw new ArgumentException(
                "A pergunta deve conter de 1 a 2000 caracteres.");
        }

        if (!Guid.TryParse(execucaoId, out var id))
            throw new ArgumentException("ExecucaoId inválido.");

        var trechos = await conhecimento.BuscarAsync(
            pergunta,
            id,
            cancellationToken);

        return JsonSerializer.Serialize(trechos);
    }
}
