using System.Text.Json;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;

namespace AulaIA;

public sealed record Trecho(
    Guid Id,
    string Titulo,
    string Fonte,
    string Conteudo,
    double Similaridade);

public sealed class ConhecimentoMcpClient(
    IConfiguration configuration)
{
    public async Task<List<Trecho>> BuscarAsync(
        string pergunta,
        Guid execucaoId,
        CancellationToken ct)
    {
        var transport = new HttpClientTransport(
            new HttpClientTransportOptions
            {
                Endpoint = new Uri(configuration["Mcp:Url"]!),
                TransportMode = HttpTransportMode.StreamableHttp
            });

        await using var client = await McpClient.CreateAsync(
            transport,
            cancellationToken: ct);

        var resultado = await client.CallToolAsync(
            "buscar_conhecimento",
            new Dictionary<string, object?>
            {
                ["pergunta"] = pergunta,
                ["execucaoId"] = execucaoId.ToString()
            },
            cancellationToken: ct);

        if (resultado.IsError == true)
        {
            throw new InvalidOperationException(
                "A ferramenta MCP retornou um erro.");
        }

        var texto = resultado.Content
            .OfType<TextContentBlock>()
            .FirstOrDefault()?.Text;

        if (string.IsNullOrWhiteSpace(texto))
        {
            throw new InvalidOperationException(
                "A ferramenta MCP não retornou conteúdo.");
        }

        return JsonSerializer.Deserialize<List<Trecho>>(texto)
            ?? throw new InvalidOperationException(
                "Resposta MCP inválida.");
    }
}