# ArquitetoIA — Sistema de Atendimento Inteligente com RAG e MCP

Projeto em **.NET 10** com implementação de **RAG (Retrieval-Augmented Generation)** integrado via **MCP (Model Context Protocol)** e **PostgreSQL + pgvector**, disponível em **duas versões** na mesma Solution:

1. **Versão Online (OpenAI)** — `Api.Conhecimento` (`:5133`) e `Api.Atendimento` (`:5065`)
2. **Versão Offline 100% Gratuita (Ollama)** — `Api.Conhecimento.Offline` (`:5134`) e `Api.Atendimento.Offline` (`:5066`)
3. **Frontend em React (`web-chat` em `:5173`)** — Interface unificada de Chat e Ingestão de Conhecimento com seletor em tempo real entre **Offline (Ollama)** e **Online (OpenAI)**

---

## Como Iniciar o Frontend em React (`web-chat`)

Com as APIs desejadas (Offline, Online ou ambas) rodando, abra um terminal e execute:

```powershell
npm --prefix .\web-chat run dev
```

O navegador abrirá automaticamente em **[http://localhost:5173](http://localhost:5173)**:
- **Seletor no Topo:** Alterne em 1 clique entre `Offline (Ollama — Gratuito)` e `Online (OpenAI)`.
- **Indicador de Status:** Mostra em tempo real se as APIs de Atendimento e Conhecimento/MCP estão `online` ou `offline`.
- **Aba "Base de Conhecimento":** Permite cadastrar documentos diretamente pela tela (já vem preenchida com um exemplo pronto para testar).
- **Aba "Chat de Atendimento":** Possui perguntas rápidas de teste e exibe os metadados completos da resposta (`informacaoEncontrada`, `encaminharParaHumano`, `custoEstimadoUsd` e os `chunks` usados como fonte via MCP).

---

## Pré-requisitos Comuns

1. [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0)
2. [Docker e Docker Compose](https://www.docker.com/)
3. **Banco de Dados (PostgreSQL + pgvector):**

Na raiz do projeto, suba os containers:

```powershell
docker compose up -d
```

Execute o script `schema.sql` (caso ainda não tenha criado as tabelas):

```powershell
Get-Content .\schema.sql | docker exec -i arquiteto-ia-postgres-1 psql -U coti -d bd_arquiteto_ia
```

> **Nota:** A versão Offline (`Api.Conhecimento.Offline`) também cria automaticamente as tabelas e a extensão `vector` ao iniciar (`EnsureDatabaseAsync`), usando a tabela `conhecimento.chunks_offline` (`VECTOR(768)`) para não conflitar com os vetores de `1536` dimensões da OpenAI.

---

## Opção A: Testando a Versão Offline 100% Gratuita (Ollama)

A versão Offline utiliza o **Ollama** rodando localmente em `http://localhost:11434/v1/` sem necessidade de chave de API e com custo `$0.00`:
- **Modelo de Embeddings (768 dimensões):** `nomic-embed-text`
- **Modelo de Chat:** `qwen3:4b-instruct`

### 1. Verificar os modelos no Ollama

Certifique-se de que os dois modelos estão instalados no seu Ollama:

```powershell
ollama list
```

Caso precise baixar em outra máquina:
```powershell
ollama pull nomic-embed-text
ollama pull qwen3:4b-instruct
```

### 2. Executar as APIs Offline

No **Visual Studio**, selecione o perfil de inicialização **"Offline (Ollama - 100% Gratuito)"** e clique em Iniciar, ou rode em **dois terminais**:

**Terminal 1 — `Api.Conhecimento.Offline` (porta `5134`):**
```powershell
dotnet run --project .\Api.Conhecimento.Offline\Api.Conhecimento.Offline.csproj
```

**Terminal 2 — `Api.Atendimento.Offline` (porta `5066`):**
```powershell
dotnet run --project .\Api.Atendimento.Offline\Api.Atendimento.Offline.csproj
```

- **Scalar UI (`Api.Conhecimento.Offline`):** [http://localhost:5134/scalar/v1](http://localhost:5134/scalar/v1)
- **Scalar UI (`Api.Atendimento.Offline`):** [http://localhost:5066/scalar/v1](http://localhost:5066/scalar/v1)

### 3. Testar a Ingestão e o Atendimento Offline

#### 3.1 — Ingerir um documento na base Offline (`POST http://localhost:5134/api/conhecimentos`)

```powershell
$bodyIngestao = @{
    titulo = "Curso C# Arquiteto de IA - COTI Informática"
    fonte  = "https://www.cotiinformatica.com.br/cursos/arquiteto-ia"
    texto  = "O curso de C# Arquiteto de IA da COTI Informática ensina desenvolvimento prático com .NET 10, RAG com PostgreSQL e pgvector, integração via Model Context Protocol (MCP), controle de custos de tokens e boas práticas de segurança com LLMs. As aulas são ao vivo online, com gravações disponíveis na plataforma do aluno e emissão de certificado ao final do treinamento."
} | ConvertTo-Json -Compress

Invoke-RestMethod -Uri "http://localhost:5134/api/conhecimentos" `
    -Method Post `
    -ContentType "application/json; charset=utf-8" `
    -Body ([System.Text.Encoding]::UTF8.GetBytes($bodyIngestao))
```

#### 3.2 — Fazer uma pergunta na API de Atendimento Offline (`POST http://localhost:5066/api/atendimentos`)

```powershell
$bodyPergunta = @{
    pergunta = "Quais tecnologias são ensinadas no curso C# Arquiteto de IA e como funcionam as aulas?"
} | ConvertTo-Json -Compress

Invoke-RestMethod -Uri "http://localhost:5066/api/atendimentos" `
    -Method Post `
    -ContentType "application/json; charset=utf-8" `
    -Body ([System.Text.Encoding]::UTF8.GetBytes($bodyPergunta))
```

#### 3.3 — Testar pergunta fora do escopo (`encaminharParaHumano = true`)

```powershell
$bodyPerguntaFora = @{
    pergunta = "Vocês oferecem curso presencial de culinária japonesa?"
} | ConvertTo-Json -Compress

Invoke-RestMethod -Uri "http://localhost:5066/api/atendimentos" `
    -Method Post `
    -ContentType "application/json; charset=utf-8" `
    -Body ([System.Text.Encoding]::UTF8.GetBytes($bodyPerguntaFora))
```

---

## Opção B: Testando a Versão Online (OpenAI)

### 1. Configurar a Chave da OpenAI (`User Secrets`)

```powershell
dotnet user-secrets set "OpenAI:ApiKey" "sk-proj-SUA_CHAVE_AQUI" --project .\Api.Conhecimento\Api.Conhecimento.csproj
dotnet user-secrets set "OpenAI:ApiKey" "sk-proj-SUA_CHAVE_AQUI" --project .\Api.Atendimento\Api.Atendimento.csproj
```

### 2. Executar as APIs Online

No **Visual Studio**, selecione o perfil **"Online (OpenAI)"**, ou execute em dois terminais:

**Terminal 1 — `Api.Conhecimento` (porta `5133`):**
```powershell
dotnet run --project .\Api.Conhecimento\Api.Conhecimento.csproj
```

**Terminal 2 — `Api.Atendimento` (porta `5065`):**
```powershell
dotnet run --project .\Api.Atendimento\Api.Atendimento.csproj
```

- **Scalar UI (`Api.Conhecimento`):** [http://localhost:5133/scalar/v1](http://localhost:5133/scalar/v1)
- **Scalar UI (`Api.Atendimento`):** [http://localhost:5065/scalar/v1](http://localhost:5065/scalar/v1)

### 3. Testar Ingestão e Atendimento Online

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

$bodyPergunta = @{
    pergunta = "Quais tecnologias são ensinadas no curso C# Arquiteto de IA e como funcionam as aulas?"
} | ConvertTo-Json -Compress

Invoke-RestMethod -Uri "http://localhost:5065/api/atendimentos" `
    -Method Post `
    -ContentType "application/json; charset=utf-8" `
    -Body ([System.Text.Encoding]::UTF8.GetBytes($bodyPergunta))
```

---

## Auditoria de Consumo no Banco de Dados

Tanto a versão Online quanto a Offline registram a quantidade de tokens de entrada e saída na tabela `auditoria.consumos` (sendo `custo_usd = 0` na versão Offline):

```powershell
docker exec -it arquiteto-ia-postgres-1 psql -U coti -d bd_arquiteto_ia -c "SELECT operacao, modelo, tokens_entrada, tokens_cache, tokens_saida, custo_usd, criado_em FROM auditoria.consumos ORDER BY criado_em DESC;"
```