using System.Text.Json;
using Npgsql;

namespace AulaIA.Offline;

public sealed record IngestRequest(
    string Titulo,
    string Fonte,
    string Texto);

public sealed record IngestResponse(
    Guid DocumentoId,
    Guid ExecucaoId,
    int QuantidadeChunks);

public sealed record Trecho(
    Guid Id,
    string Titulo,
    string Fonte,
    string Conteudo,
    double Similaridade);

public sealed class ConhecimentoService(
    NpgsqlDataSource dataSource,
    OllamaGateway ollama,
    IConfiguration configuration)
{
    private static List<string> DividirEmChunks(string texto)
    {
        const int tamanho = 1000;
        const int sobreposicao = 150;

        texto = texto.Replace("\r\n", "\n").Trim();

        var chunks = new List<string>();

        for (var inicio = 0; inicio < texto.Length;)
        {
            var fim = Math.Min(inicio + tamanho, texto.Length);
            var chunk = texto[inicio..fim].Trim();

            if (!string.IsNullOrWhiteSpace(chunk))
                chunks.Add(chunk);

            if (fim == texto.Length)
                break;

            inicio = fim - sobreposicao;
        }

        return chunks;
    }

    private async Task<string[]> GerarEmbeddingsAsync(
        string[] textos,
        Guid execucaoId,
        string operacao,
        CancellationToken ct)
    {
        var response = await ollama.PostAsync(
            "embeddings",
            new
            {
                model = ollama.EmbeddingModel,
                input = textos
            },
            execucaoId,
            operacao,
            embedding: true,
            ct: ct);

        var dimensoesEsperadas = ollama.EmbeddingDimensions;

        var vetores = response.GetProperty("data")
            .EnumerateArray()
            .OrderBy(item => item.GetProperty("index").GetInt32())
            .Select(item =>
            {
                var vetor = item.GetProperty("embedding");

                if (vetor.GetArrayLength() != dimensoesEsperadas)
                    throw new InvalidOperationException(
                        $"Dimensão do embedding ({vetor.GetArrayLength()}) incompatível com o esperado ({dimensoesEsperadas}).");

                return vetor.GetRawText();
            })
            .ToArray();

        if (vetores.Length != textos.Length)
            throw new InvalidOperationException(
                "Quantidade inesperada de embeddings.");

        return vetores;
    }

    public async Task<IngestResponse> IngerirAsync(
        IngestRequest request,
        CancellationToken ct)
    {
        var documentoId = Guid.NewGuid();
        var execucaoId = Guid.NewGuid();

        var chunks = DividirEmChunks(request.Texto);

        var vetores = await GerarEmbeddingsAsync(
            chunks.ToArray(),
            execucaoId,
            "ingestao_embedding_offline",
            ct);

        await using var connection =
            await dataSource.OpenConnectionAsync(ct);

        await using var transaction =
            await connection.BeginTransactionAsync(ct);

        await using (var cmd = new NpgsqlCommand("""
            INSERT INTO conhecimento.documentos(id, titulo, fonte)
            VALUES (@id, @titulo, @fonte)
            """, connection, transaction))
        {
            cmd.Parameters.AddWithValue("id", documentoId);
            cmd.Parameters.AddWithValue("titulo", request.Titulo.Trim());
            cmd.Parameters.AddWithValue("fonte", request.Fonte.Trim());

            await cmd.ExecuteNonQueryAsync(ct);
        }

        for (var i = 0; i < chunks.Count; i++)
        {
            await using var cmd = new NpgsqlCommand("""
                INSERT INTO conhecimento.chunks_offline (
                    id, documento_id, ordem, conteudo,
                    modelo_embedding, embedding
                )
                VALUES (
                    @id, @documento, @ordem, @conteudo,
                    @modelo, CAST(@vetor AS vector)
                )
                """, connection, transaction);

            cmd.Parameters.AddWithValue("id", Guid.NewGuid());
            cmd.Parameters.AddWithValue("documento", documentoId);
            cmd.Parameters.AddWithValue("ordem", i);
            cmd.Parameters.AddWithValue("conteudo", chunks[i]);
            cmd.Parameters.AddWithValue("modelo", ollama.EmbeddingModel);
            cmd.Parameters.AddWithValue("vetor", vetores[i]);

            await cmd.ExecuteNonQueryAsync(ct);
        }

        await transaction.CommitAsync(ct);

        return new IngestResponse(
            documentoId,
            execucaoId,
            chunks.Count);
    }

    public async Task<List<Trecho>> BuscarAsync(
        string pergunta,
        Guid execucaoId,
        CancellationToken ct)
    {
        var vetores = await GerarEmbeddingsAsync(
            [pergunta],
            execucaoId,
            "consulta_embedding_offline",
            ct);

        var topK = Math.Clamp(
            configuration.GetValue<int>("Rag:TopK"), 1, 10);

        var minimo =
            configuration.GetValue<double>("Rag:MinSimilarity");

        await using var cmd = dataSource.CreateCommand("""
            SELECT
                c.id,
                d.titulo,
                d.fonte,
                c.conteudo,
                1 - (c.embedding <=> CAST(@vetor AS vector))
                    AS similaridade
            FROM conhecimento.chunks_offline c
            JOIN conhecimento.documentos d
                ON d.id = c.documento_id
            WHERE c.modelo_embedding = @modelo
            ORDER BY c.embedding <=> CAST(@vetor AS vector)
            LIMIT @topK
            """);

        cmd.Parameters.AddWithValue("vetor", vetores[0]);
        cmd.Parameters.AddWithValue("modelo", ollama.EmbeddingModel);
        cmd.Parameters.AddWithValue("topK", topK);

        var resultado = new List<Trecho>();

        await using var reader = await cmd.ExecuteReaderAsync(ct);

        while (await reader.ReadAsync(ct))
        {
            var similaridade = reader.GetDouble(4);

            if (similaridade < minimo)
                continue;

            resultado.Add(new Trecho(
                reader.GetGuid(0),
                reader.GetString(1),
                reader.GetString(2),
                reader.GetString(3),
                similaridade));
        }

        return resultado;
    }
}
