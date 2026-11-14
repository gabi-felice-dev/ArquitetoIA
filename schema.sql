CREATE EXTENSION IF NOT EXISTS vector;

CREATE SCHEMA IF NOT EXISTS conhecimento;
CREATE SCHEMA IF NOT EXISTS auditoria;

CREATE TABLE conhecimento.documentos (
    id UUID PRIMARY KEY,
    titulo VARCHAR(200) NOT NULL,
    fonte VARCHAR(500) NOT NULL,
    criado_em TIMESTAMPTZ NOT NULL DEFAULT NOW()
);

CREATE TABLE conhecimento.chunks (
    id UUID PRIMARY KEY,
    documento_id UUID NOT NULL
        REFERENCES conhecimento.documentos(id) ON DELETE CASCADE,
    ordem INTEGER NOT NULL,
    conteudo TEXT NOT NULL,
    modelo_embedding VARCHAR(100) NOT NULL,
    embedding VECTOR(1536) NOT NULL,
    UNIQUE(documento_id, ordem)
);

CREATE INDEX ix_chunks_embedding
ON conhecimento.chunks
USING hnsw (embedding vector_cosine_ops);

CREATE TABLE auditoria.consumos (
    id UUID PRIMARY KEY,
    execucao_id UUID NOT NULL,
    operacao VARCHAR(50) NOT NULL,
    modelo VARCHAR(100) NOT NULL,
    tokens_entrada INTEGER NOT NULL,
    tokens_cache INTEGER NOT NULL,
    tokens_saida INTEGER NOT NULL,
    preco_entrada_milhao NUMERIC(18,8) NOT NULL,
    preco_cache_milhao NUMERIC(18,8) NOT NULL,
    preco_saida_milhao NUMERIC(18,8) NOT NULL,
    custo_usd NUMERIC(18,10) NOT NULL,
    criado_em TIMESTAMPTZ NOT NULL DEFAULT NOW()
);

CREATE INDEX ix_consumos_execucao
ON auditoria.consumos(execucao_id);