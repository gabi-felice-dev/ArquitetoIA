# ArquitetoIA — Sistema de Atendimento Inteligente com RAG e MCP

Projeto em **.NET 10** composto por duas APIs (`Api.Conhecimento` e `Api.Atendimento`) que implementam um fluxo de **RAG (Retrieval-Augmented Generation)** integrado via **MCP (Model Context Protocol)**, utilizando **PostgreSQL + pgvector** para busca vetorial e **OpenAI** para geração de embeddings e respostas estruturadas, além de auditoria de consumo e custos em dólares (USD).

---

## Arquitetura do Projeto

- **`Api.Conhecimento` (`http://localhost:5133`)**
  - Responsável pela ingestão de documentos (`POST /api/conhecimentos`), divisão em *chunks*, geração de embeddings (`text-embedding-3-small`) e armazenamento no PostgreSQL (`pgvector`).
  - Expõe um servidor **MCP** em `/mcp` com a ferramenta `buscar_conhecimento` para busca semântica por similaridade de cosseno.
- **`Api.Atendimento` (`http://localhost:5065`)**
  - Recebe perguntas dos usuários (`POST /api/atendimentos`), consulta obrigatoriamente a base de conhecimento via cliente **MCP** (`ConhecimentoMcpClient`) e gera respostas estruturadas (JSON Schema estrito) usando a OpenAI.
  - Valida citações de fontes (*chunks*) e calcula o custo estimado total da execução.
- **Banco de Dados (`PostgreSQL 16 + pgvector`)**
  - Esquema `conhecimento`: tabelas `documentos` e `chunks` (com índice HNSW vetorial).
  - Esquema `auditoria`: tabela `consumos` para rastrear tokens de entrada, cache, saída e custo em USD por operação.

---

## Pré-requisitos

1. [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0)
2. [Docker e Docker Compose](https://www.docker.com/)
3. Uma chave de API da **OpenAI** (`OpenAI:ApiKey`)

---

## Passo a Passo para Executar e Testar

### 1. Subir o Banco de Dados (PostgreSQL + pgvector)

Na raiz do projeto, inicie os containers do Docker:

```powershell
docker compose up -d
```

- **PostgreSQL:** `localhost:5437` (Banco: `bd_arquiteto_ia` | Usuário: `coti` | Senha: `coti`)
- **pgAdmin 4:** `http://localhost:5053` (Login: `coti@email.com` | Senha: `coti`)

### 2. Criar as Tabelas e Extensão `vector` (`schema.sql`)

Execute o script `schema.sql` diretamente no container do PostgreSQL para criar a extensão `vector`, os esquemas `conhecimento` e `auditoria`, e as respectivas tabelas:

```powershell
Get-Content .\schema.sql | docker exec -i arquiteto-ia-postgres-1 psql -U coti -d bd_arquiteto_ia
```

*(Alternativamente, você pode conectar pelo pgAdmin em `http://localhost:5053` e executar o conteúdo do arquivo `schema.sql`).*

---

### 3. Configurar a Chave da OpenAI (`User Secrets`)

Os dois projetos precisam da chave `OpenAI:ApiKey`. Configure usando o **User Secrets** do .NET para não expor sua chave nos arquivos versionados:

```powershell
dotnet user-secrets set "OpenAI:ApiKey" "sk-proj-SUA_CHAVE_AQUI" --project .\Api.Conhecimento\Api.Conhecimento.csproj
dotnet user-secrets set "OpenAI:ApiKey" "sk-proj-SUA_CHAVE_AQUI" --project .\Api.Atendimento\Api.Atendimento.csproj
```

> **Observação sobre o modelo de Chat:** Verifique nos arquivos `Api.Atendimento/appsettings.json` e `Api.Conhecimento/appsettings.json` a chave `"OpenAI:ChatModel"` (ex.: `"gpt-4o-mini"` ou `"gpt-6-luna"` conforme configurado na sua conta OpenAI).

---

### 4. Compilar e Executar as APIs

Você pode iniciar ambos os projetos pelo Visual Studio (usando o perfil de múltiplos projetos já configurado na *Solution*) ou abrir **dois terminais** separados:

**Terminal 1 — `Api.Conhecimento` (porta `5133`):**
```powershell
dotnet run --project .\Api.Conhecimento\Api.Conhecimento.csproj
```

**Terminal 2 — `Api.Atendimento` (porta `5065`):**
```powershell
dotnet run --project .\Api.Atendimento\Api.Atendimento.csproj
```

#### Documentação Interativa (Scalar UI)
Com as APIs rodando, você pode acessar a interface do **Scalar** no navegador:
- **Api.Conhecimento:** [http://localhost:5133/scalar/v1](http://localhost:5133/scalar/v1)
- **Api.Atendimento:** [http://localhost:5065/scalar/v1](http://localhost:5065/scalar/v1)

---

### 5. Testar os Endpoints

#### Passo 5.1 — Verificar a saúde dos serviços (`Healthcheck`)

```powershell
Invoke-RestMethod -Uri "http://localhost:5133/health" -Method Get
Invoke-RestMethod -Uri "http://localhost:5065/health" -Method Get
```

#### Passo 5.2 — Ingerir conhecimento na base (`POST /api/conhecimentos`)

Envie um documento de teste para a `Api.Conhecimento` gerar os *chunks* e salvar os embeddings no PostgreSQL:

```powershell
$bodyIngestao = @{
    titulo = "Curso C# Arquiteto de IA - COTI Informática"
    fonte  = "https://www.cotiinformatica.com.br/cursos/arquiteto-ia"
    texto  = "O curso de C# Arquiteto de IA da COTI Informática ensina desenvolvimento prático com .NET 10, RAG com PostgreSQL e pgvector, integração via Model Context Protocol (MCP), controle de custos de tokens e boas práticas de segurança com LLMs. As aulas são ao vivo online, com gravações disponíveis na plataforma do aluno e emissão de certificado ao final do treinamento."
} | ConvertTo-Json -Compress

Invoke-RestMethod -Uri "http://localhost:5133/api/conhecimentos" `
    -Method Post `
    -ContentType "application/json; charset=utf-8" `
    -Body ([System.Text.Encoding]::UTF8.GetBytes($bodyIngestao))
```

**Resposta esperada:**
```json
{
  "documentoId": "...",
  "execucaoId": "...",
  "quantidadeChunks": 1
}
```

#### Passo 5.3 — Realizar uma pergunta com resposta na base (`POST /api/atendimentos`)

Agora teste a `Api.Atendimento` fazendo uma pergunta sobre o conteúdo ingerido. Ela consultará a `Api.Conhecimento` via **MCP** (`http://localhost:5133/mcp`) e gerará a resposta estruturada:

```powershell
$bodyPergunta = @{
    pergunta = "Quais tecnologias são ensinadas no curso C# Arquiteto de IA e como funcionam as aulas?"
} | ConvertTo-Json -Compress

Invoke-RestMethod -Uri "http://localhost:5065/api/atendimentos" `
    -Method Post `
    -ContentType "application/json; charset=utf-8" `
    -Body ([System.Text.Encoding]::UTF8.GetBytes($bodyPergunta))
```

**Resposta esperada:**
- `informacaoEncontrada`: `true`
- `encaminharParaHumano`: `false`
- `fontes`: lista contendo o `chunkId`, `titulo` e `fonte` do documento utilizado
- `custoEstimadoUsd`: valor calculado da chamada de chat

#### Passo 5.4 — Testar pergunta fora da base (encaminhamento para atendimento humano)

Faça uma pergunta cujo assunto **não** esteja cadastrado na base de conhecimento:

```powershell
$bodyPerguntaFora = @{
    pergunta = "Vocês oferecem curso presencial de culinária japonesa?"
} | ConvertTo-Json -Compress

Invoke-RestMethod -Uri "http://localhost:5065/api/atendimentos" `
    -Method Post `
    -ContentType "application/json; charset=utf-8" `
    -Body ([System.Text.Encoding]::UTF8.GetBytes($bodyPerguntaFora))
```

**Resposta esperada:**
- `informacaoEncontrada`: `false`
- `encaminharParaHumano`: `true`
- `fontes`: `[]`

---

### 6. Auditar o Consumo no Banco de Dados

Para verificar os registros de tokens e custos gravados na tabela `auditoria.consumos`, execute:

```powershell
docker exec -it arquiteto-ia-postgres-1 psql -U coti -d bd_arquiteto_ia -c "SELECT operacao, modelo, tokens_entrada, tokens_cache, tokens_saida, custo_usd, criado_em FROM auditoria.consumos ORDER BY criado_em DESC;"
```