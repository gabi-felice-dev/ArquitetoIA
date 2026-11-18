using MongoDB.Bson;
using MongoDB.Bson.Serialization.Attributes;
using MongoDB.Driver;

namespace AulaIA;

// Mensagem no formato da API de chat (role/content).
public sealed record MensagemChat(string Role, string Content);

// Identificação simples do usuário pelo nome enviado no request.
// A Chave normaliza o nome ("  Maria   SOUZA " -> "maria souza")
// para que pequenas diferenças de digitação caiam no mesmo histórico.
public sealed record Cliente(string Nome)
{
    public string Chave { get; } = string.Join(' ',
        Nome.Trim()
            .ToLowerInvariant()
            .Split(' ', StringSplitOptions.RemoveEmptyEntries));
}

// Um documento por mensagem (user ou assistant) na coleção "conversas".
public sealed class MensagemConversa
{
    [BsonId]
    public ObjectId Id { get; set; }

    // Chave normalizada do nome: é por ela que buscamos o histórico.
    [BsonElement("usuario")]
    public string Usuario { get; set; } = "";

    // Nome como o usuário digitou (para exibição).
    [BsonElement("nome")]
    public string Nome { get; set; } = "";

    // "user" ou "assistant" — exatamente os papéis da API de chat.
    [BsonElement("papel")]
    public string Papel { get; set; } = "";

    [BsonElement("conteudo")]
    public string Conteudo { get; set; } = "";

    // Liga a mensagem à auditoria de custo no Postgres.
    [BsonElement("execucaoId")]
    [BsonRepresentation(BsonType.String)]
    public Guid ExecucaoId { get; set; }

    [BsonElement("provedor")]
    public string? Provedor { get; set; }

    [BsonElement("criadoEm")]
    public DateTime CriadoEm { get; set; }
}

public sealed class MemoriaConversaRepository
{
    private readonly IMongoCollection<MensagemConversa> _conversas;
    private readonly int _maxMensagens;
    private readonly int _diasRetencao;

    public MemoriaConversaRepository(
        IMongoClient client,
        IConfiguration configuration)
    {
        var database = client.GetDatabase(
            configuration["Mongo:Database"] ?? "bd_atendimento");

        _conversas = database.GetCollection<MensagemConversa>("conversas");
        _maxMensagens = configuration.GetValue("Memoria:MaxMensagens", 10);
        _diasRetencao = configuration.GetValue("Memoria:DiasRetencao", 30);
    }

    public async Task CriarIndicesAsync(CancellationToken ct)
    {
        var chaves = Builders<MensagemConversa>.IndexKeys;

        await _conversas.Indexes.CreateManyAsync(
        [
            // Consulta principal: últimas mensagens de um usuário.
            new CreateIndexModel<MensagemConversa>(
                chaves.Ascending(m => m.Usuario)
                      .Descending(m => m.CriadoEm),
                new CreateIndexOptions { Name = "ix_usuario_data" }),

            // TTL: o próprio Mongo apaga mensagens antigas (LGPD / custo).
            new CreateIndexModel<MensagemConversa>(
                chaves.Ascending(m => m.CriadoEm),
                new CreateIndexOptions
                {
                    Name = "ttl_criado_em",
                    ExpireAfter = TimeSpan.FromDays(_diasRetencao)
                })
        ], ct);
    }

    // Janela deslizante: só as N últimas mensagens, em ordem cronológica.
    public async Task<List<MensagemConversa>> UltimasAsync(
        Cliente cliente,
        CancellationToken ct)
    {
        var ultimas = await _conversas
            .Find(m => m.Usuario == cliente.Chave)
            .SortByDescending(m => m.CriadoEm)
            .Limit(_maxMensagens)
            .ToListAsync(ct);

        ultimas.Reverse();
        return ultimas;
    }

    public Task SalvarTurnoAsync(
        Cliente cliente,
        Guid execucaoId,
        string pergunta,
        string resposta,
        string? provedor,
        CancellationToken ct)
    {
        var agora = DateTime.UtcNow;

        return _conversas.InsertManyAsync(
        [
            new MensagemConversa
            {
                Usuario = cliente.Chave,
                Nome = cliente.Nome.Trim(),
                Papel = "user",
                Conteudo = pergunta,
                ExecucaoId = execucaoId,
                CriadoEm = agora
            },
            new MensagemConversa
            {
                Usuario = cliente.Chave,
                Nome = cliente.Nome.Trim(),
                Papel = "assistant",
                Conteudo = resposta,
                ExecucaoId = execucaoId,
                Provedor = provedor,
                CriadoEm = agora.AddMilliseconds(1)
            }
        ], cancellationToken: ct);
    }

    public Task LimparAsync(Cliente cliente, CancellationToken ct) =>
        _conversas.DeleteManyAsync(m => m.Usuario == cliente.Chave, ct);
}

public static class MemoriaConversaSetup
{
    public static void AddMemoriaConversacional(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        var connectionString =
            configuration.GetConnectionString("Mongo")
            ?? throw new InvalidOperationException(
                "ConnectionStrings:Mongo não configurada.");

        // MongoClient é thread-safe e deve ser singleton.
        services.AddSingleton<IMongoClient>(
            _ => new MongoClient(connectionString));

        services.AddSingleton<MemoriaConversaRepository>();
    }
}