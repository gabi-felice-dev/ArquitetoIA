import { useState, useEffect, useRef } from 'react'
import logoImg from '../images/logo.png'
import iconImg from '../images/icon.png'

const ENVIRONMENTS = {
  offline: {
    id: 'offline',
    name: 'Ollama Local',
    subtitle: '100% Gratuito · Offline',
    models: 'qwen3:4b-instruct · nomic-embed-text',
    atendimentoUrl: 'http://localhost:5066',
    conhecimentoUrl: 'http://localhost:5134',
  },
  online: {
    id: 'online',
    name: 'OpenAI Cloud',
    subtitle: 'API Oficial · Online',
    models: 'gpt-6-luna · text-embedding-3-small',
    atendimentoUrl: 'http://localhost:5065',
    conhecimentoUrl: 'http://localhost:5133',
  },
}

const SUGGESTIONS = [
  {
    title: 'Tecnologias do curso',
    desc: 'O que é ensinado no curso C# Arquiteto de IA?',
    prompt:
      'Quais tecnologias são ensinadas no curso C# Arquiteto de IA e como funcionam as aulas?',
  },
  {
    title: 'Aulas e certificado',
    desc: 'Gravações na plataforma e emissão de certificado',
    prompt: 'O curso possui gravações das aulas e emite certificado ao final?',
  },
  {
    title: 'Teste fora do escopo',
    desc: 'Validar encaminhamento para atendimento humano',
    prompt: 'Vocês oferecem curso presencial de culinária japonesa?',
  },
]

const SAMPLE_DOCUMENT = {
  titulo: 'Curso C# Arquiteto de IA - COTI Informática',
  fonte: 'https://www.cotiinformatica.com.br/cursos/arquiteto-ia',
  texto:
    'O curso de C# Arquiteto de IA da COTI Informática ensina desenvolvimento prático com .NET 10, RAG com PostgreSQL e pgvector, integração via Model Context Protocol (MCP), controle de custos de tokens e boas práticas de segurança com LLMs. As aulas são ao vivo online, com gravações disponíveis na plataforma do aluno e emissão de certificado ao final do treinamento.',
}

export default function App() {
  const [envKey, setEnvKey] = useState('offline')
  const [health, setHealth] = useState({
    atendimento: 'checking',
    conhecimento: 'checking',
  })

  // Chat state
  const [messages, setMessages] = useState([])
  const [question, setQuestion] = useState('')
  const [loadingChat, setLoadingChat] = useState(false)
  const [expandedSources, setExpandedSources] = useState({})
  const messagesEndRef = useRef(null)

  // Modal state for Knowledge Base
  const [isKnowledgeModalOpen, setIsKnowledgeModalOpen] = useState(false)
  const [titulo, setTitulo] = useState(SAMPLE_DOCUMENT.titulo)
  const [fonte, setFonte] = useState(SAMPLE_DOCUMENT.fonte)
  const [texto, setTexto] = useState(SAMPLE_DOCUMENT.texto)
  const [loadingIngest, setLoadingIngest] = useState(false)
  const [ingestResult, setIngestResult] = useState(null)
  const [ingestError, setIngestError] = useState(null)

  const currentEnv = ENVIRONMENTS[envKey]

  const checkHealth = async () => {
    setHealth({ atendimento: 'checking', conhecimento: 'checking' })

    const checkUrl = async (url) => {
      try {
        const res = await fetch(`${url}/health`, { method: 'GET' })
        return res.ok ? 'online' : 'offline'
      } catch {
        return 'offline'
      }
    }

    const [atendimentoStatus, conhecimentoStatus] = await Promise.all([
      checkUrl(currentEnv.atendimentoUrl),
      checkUrl(currentEnv.conhecimentoUrl),
    ])

    setHealth({
      atendimento: atendimentoStatus,
      conhecimento: conhecimentoStatus,
    })
  }

  useEffect(() => {
    checkHealth()
  }, [envKey])

  useEffect(() => {
    messagesEndRef.current?.scrollIntoView({ behavior: 'smooth' })
  }, [messages, loadingChat])

  const toggleSources = (msgId) => {
    setExpandedSources((prev) => ({
      ...prev,
      [msgId]: !prev[msgId],
    }))
  }

  const handleSendQuestion = async (customQuestion) => {
    const textToSend = (customQuestion ?? question).trim()
    if (!textToSend || loadingChat) return

    if (!customQuestion) setQuestion('')

    const userMsg = {
      id: crypto.randomUUID(),
      role: 'user',
      env: envKey,
      text: textToSend,
    }

    setMessages((prev) => [...prev, userMsg])
    setLoadingChat(true)

    try {
      const response = await fetch(
        `${currentEnv.atendimentoUrl}/api/atendimentos`,
        {
          method: 'POST',
          headers: { 'Content-Type': 'application/json; charset=utf-8' },
          body: JSON.stringify({ pergunta: textToSend }),
        }
      )

      if (!response.ok) {
        const errText = await response.text()
        throw new Error(
          `HTTP ${response.status}: ${errText || 'Erro ao consultar API de Atendimento.'}`
        )
      }

      const data = await response.json()

      setMessages((prev) => [
        ...prev,
        {
          id: crypto.randomUUID(),
          role: 'assistant',
          env: envKey,
          text: data.resposta,
          meta: {
            execucaoId: data.execucaoId,
            informacaoEncontrada: data.informacaoEncontrada,
            encaminharParaHumano: data.encaminharParaHumano,
            fontes: data.fontes || [],
            custoEstimadoUsd: data.custoEstimadoUsd ?? 0,
          },
        },
      ])
    } catch (err) {
      setMessages((prev) => [
        ...prev,
        {
          id: crypto.randomUUID(),
          role: 'assistant',
          env: envKey,
          isError: true,
          text: `Não foi possível conectar em ${currentEnv.atendimentoUrl}. Certifique-se de que as APIs do modo ${currentEnv.name} estão em execução.\n\nDetalhe: ${err.message}`,
        },
      ])
    } finally {
      setLoadingChat(false)
    }
  }

  const handleIngest = async (e) => {
    e.preventDefault()
    if (loadingIngest) return

    setLoadingIngest(true)
    setIngestResult(null)
    setIngestError(null)

    try {
      const response = await fetch(
        `${currentEnv.conhecimentoUrl}/api/conhecimentos`,
        {
          method: 'POST',
          headers: { 'Content-Type': 'application/json; charset=utf-8' },
          body: JSON.stringify({ titulo, fonte, texto }),
        }
      )

      if (!response.ok) {
        const errText = await response.text()
        throw new Error(`HTTP ${response.status}: ${errText}`)
      }

      const data = await response.json()
      setIngestResult({
        ...data,
        envName: currentEnv.name,
      })
    } catch (err) {
      setIngestError(
        `Falha ao conectar em ${currentEnv.conhecimentoUrl}: ${err.message}`
      )
    } finally {
      setLoadingIngest(false)
    }
  }

  const handleKeyDown = (e) => {
    if (e.key === 'Enter' && !e.shiftKey) {
      e.preventDefault()
      handleSendQuestion()
    }
  }

  return (
    <div className="layout">
      {/* Left Sidebar (ChatGPT / Claude style) */}
      <aside className="sidebar">
        <div className="sidebar-top">
          <div className="brand">
            <img src={logoImg} alt="Arquiteto IA" className="brand-logo" />
            <div className="brand-text">
              <span className="brand-title">Arquiteto IA</span>
              <span className="brand-tag">RAG + MCP</span>
            </div>
          </div>

          <button
            type="button"
            className="new-chat-btn"
            onClick={() => setMessages([])}
          >
            <span>＋</span> Nova conversa
          </button>

          <div className="sidebar-section">
            <span className="section-label">Motor de IA</span>
            <div className="engine-selector">
              {Object.values(ENVIRONMENTS).map((env) => (
                <button
                  key={env.id}
                  type="button"
                  className={`engine-card ${envKey === env.id ? 'active' : ''}`}
                  onClick={() => setEnvKey(env.id)}
                >
                  <div className="engine-card-header">
                    <span className="engine-name">{env.name}</span>
                    <span className={`radio-dot ${envKey === env.id ? 'checked' : ''}`} />
                  </div>
                  <span className="engine-sub">{env.subtitle}</span>
                </button>
              ))}
            </div>
          </div>

          <div className="sidebar-section">
            <div className="section-header-row">
              <span className="section-label">Status dos Serviços</span>
              <button
                type="button"
                className="icon-refresh-btn"
                onClick={checkHealth}
                title="Atualizar status"
              >
                ↻
              </button>
            </div>
            <div className="status-card">
              <div className="status-row">
                <span className="status-name">API Atendimento</span>
                <span className={`status-badge ${health.atendimento}`}>
                  <span className="status-dot" />
                  {health.atendimento === 'online'
                    ? 'Online'
                    : health.atendimento === 'checking'
                    ? '...'
                    : 'Offline'}
                </span>
              </div>
              <div className="status-row">
                <span className="status-name">Servidor MCP / RAG</span>
                <span className={`status-badge ${health.conhecimento}`}>
                  <span className="status-dot" />
                  {health.conhecimento === 'online'
                    ? 'Online'
                    : health.conhecimento === 'checking'
                    ? '...'
                    : 'Offline'}
                </span>
              </div>
              <div className="status-models">{currentEnv.models}</div>
            </div>
          </div>
        </div>

        <div className="sidebar-bottom">
          <button
            type="button"
            className="knowledge-btn"
            onClick={() => {
              setIngestResult(null)
              setIngestError(null)
              setIsKnowledgeModalOpen(true)
            }}
          >
            <div className="knowledge-btn-left">
              <span className="knowledge-icon">📚</span>
              <div>
                <div className="knowledge-title">Base de Conhecimento</div>
                <div className="knowledge-sub">Ingerir documentos no RAG</div>
              </div>
            </div>
            <span className="chevron">›</span>
          </button>
        </div>
      </aside>

      {/* Main Chat Area */}
      <main className="chat-main">
        {/* Top subtle bar */}
        <header className="chat-topbar">
          <div className="topbar-left">
            <span className="topbar-model-pill">
              Modo ativo: <strong>{currentEnv.name}</strong>
            </span>
            <span className="topbar-ports">
              Atendimento {currentEnv.atendimentoUrl.replace('http://localhost', '')} · MCP{' '}
              {currentEnv.conhecimentoUrl.replace('http://localhost', '')}
            </span>
          </div>
          <button
            type="button"
            className="topbar-Action-btn"
            onClick={() => setIsKnowledgeModalOpen(true)}
          >
            + Adicionar Conhecimento
          </button>
        </header>

        {/* Messages or Empty State */}
        <div className="chat-scroll-area">
          {messages.length === 0 ? (
            <div className="empty-state">
              <div className="empty-logo-ring">
                <img src={logoImg} alt="Corujinha IA" />
              </div>
              <h2>Como posso ajudar você hoje?</h2>
              <p>
                Consulto a base de conhecimento da COTI Informática via{' '}
                <strong>MCP</strong> usando <strong>{currentEnv.name}</strong>.
              </p>

              <div className="suggestions-grid">
                {SUGGESTIONS.map((item, idx) => (
                  <button
                    key={idx}
                    type="button"
                    className="suggestion-card"
                    onClick={() => handleSendQuestion(item.prompt)}
                  >
                    <span className="suggestion-title">{item.title}</span>
                    <span className="suggestion-desc">{item.desc}</span>
                  </button>
                ))}
              </div>
            </div>
          ) : (
            <div className="messages-container">
              {messages.map((msg) => (
                <div
                  key={msg.id}
                  className={`msg-wrapper ${msg.role === 'user' ? 'user' : 'assistant'}`}
                >
                  {msg.role === 'assistant' && (
                    <img
                      src={iconImg}
                      alt="Assistente"
                      className="msg-avatar"
                    />
                  )}

                  <div className="msg-content">
                    <div
                      className={`msg-bubble ${msg.isError ? 'error' : ''}`}
                    >
                      {msg.text}
                    </div>

                    {msg.meta && (
                      <div className="msg-footer">
                        <span
                          className={`pill ${
                            msg.meta.informacaoEncontrada ? 'ok' : 'warn'
                          }`}
                        >
                          {msg.meta.informacaoEncontrada
                            ? '✓ Base encontrada'
                            : '⚠ Sem base suficiente'}
                        </span>

                        {msg.meta.encaminharParaHumano && (
                          <span className="pill human">
                            Encaminhar para humano
                          </span>
                        )}

                        <span className="pill neutral">
                          {msg.env === 'offline'
                            ? 'Ollama · Gratuito ($0)'
                            : `OpenAI · $${Number(msg.meta.custoEstimadoUsd).toFixed(6)}`}
                        </span>

                        {msg.meta.fontes && msg.meta.fontes.length > 0 && (
                          <button
                            type="button"
                            className="sources-toggle-btn"
                            onClick={() => toggleSources(msg.id)}
                          >
                            {expandedSources[msg.id]
                              ? 'Ocultar fontes ▲'
                              : `Ver ${msg.meta.fontes.length} fonte(s) ▼`}
                          </button>
                        )}

                        {expandedSources[msg.id] &&
                          msg.meta.fontes &&
                          msg.meta.fontes.length > 0 && (
                            <div className="sources-drawer">
                              {msg.meta.fontes.map((f) => (
                                <div key={f.chunkId} className="source-row">
                                  <div className="source-row-title">{f.titulo}</div>
                                  <div className="source-row-meta">
                                    <span>{f.fonte}</span>
                                    <code>Chunk: {f.chunkId}</code>
                                  </div>
                                </div>
                              ))}
                            </div>
                          )}
                      </div>
                    )}
                  </div>
                </div>
              ))}

              {loadingChat && (
                <div className="msg-wrapper assistant">
                  <img
                    src={iconImg}
                    alt="Assistente"
                    className="msg-avatar thinking"
                  />
                  <div className="msg-content">
                    <div className="msg-bubble loading-bubble">
                      <span className="typing-dots">
                        <span />
                        <span />
                        <span />
                      </span>
                      Consultando MCP e gerando resposta ({currentEnv.name})...
                    </div>
                  </div>
                </div>
              )}
              <div ref={messagesEndRef} />
            </div>
          )}
        </div>

        {/* Bottom Floating Input Box */}
        <div className="composer-container">
          <form
            className="composer-box"
            onSubmit={(e) => {
              e.preventDefault()
              handleSendQuestion()
            }}
          >
            <textarea
              rows={1}
              placeholder={`Envie uma pergunta para ${currentEnv.name}...`}
              value={question}
              onChange={(e) => setQuestion(e.target.value)}
              onKeyDown={handleKeyDown}
              disabled={loadingChat}
              maxLength={2000}
            />
            <button
              type="submit"
              className="send-btn"
              disabled={loadingChat || !question.trim()}
              title="Enviar pergunta"
            >
              ↑
            </button>
          </form>
          <div className="composer-Caption">
            Respostas baseadas estritamente nos documentos cadastrados na Base de Conhecimento via MCP.
          </div>
        </div>
      </main>

      {/* Modal: Base de Conhecimento */}
      {isKnowledgeModalOpen && (
        <div
          className="modal-backdrop"
          onClick={() => setIsKnowledgeModalOpen(false)}
        >
          <div
            className="modal-card"
            onClick={(e) => e.stopPropagation()}
          >
            <div className="modal-header">
              <div className="modal-title-wrap">
                <img src={iconImg} alt="" className="modal-icon" />
                <div>
                  <h3>Base de Conhecimento ({currentEnv.name})</h3>
                  <p>
                    Envia para <code>POST {currentEnv.conhecimentoUrl}/api/conhecimentos</code>
                  </p>
                </div>
              </div>
              <button
                type="button"
                className="modal-close"
                onClick={() => setIsKnowledgeModalOpen(false)}
              >
                ✕
              </button>
            </div>

            <form onSubmit={handleIngest} className="modal-form">
              <div className="form-top-actions">
                <span className="form-hint">
                  O texto será dividido em chunks e vetorizado no PostgreSQL (pgvector).
                </span>
                <button
                  type="button"
                  className="ghost-btn"
                  onClick={() => {
                    setTitulo(SAMPLE_DOCUMENT.titulo)
                    setFonte(SAMPLE_DOCUMENT.fonte)
                    setTexto(SAMPLE_DOCUMENT.texto)
                  }}
                >
                  Restaurar exemplo padrão
                </button>
              </div>

              <label className="field">
                <span>Título do documento</span>
                <input
                  type="text"
                  value={titulo}
                  onChange={(e) => setTitulo(e.target.value)}
                  maxLength={200}
                  required
                />
              </label>

              <label className="field">
                <span>Fonte / URL de referência</span>
                <input
                  type="text"
                  value={fonte}
                  onChange={(e) => setFonte(e.target.value)}
                  maxLength={500}
                  required
                />
              </label>

              <label className="field">
                <span>Conteúdo</span>
                <textarea
                  rows={5}
                  value={texto}
                  onChange={(e) => setTexto(e.target.value)}
                  maxLength={20000}
                  required
                />
              </label>

              {ingestResult && (
                <div className="feedback-box ok">
                  <strong>✓ Documento vetorizado com sucesso em {ingestResult.envName}!</strong>
                  <span>
                    Chunks gerados: <strong>{ingestResult.quantidadeChunks}</strong> · ID:{' '}
                    <code>{ingestResult.documentoId}</code>
                  </span>
                </div>
              )}

              {ingestError && (
                <div className="feedback-box err">
                  <strong>Erro ao ingerir documento:</strong>
                  <span>{ingestError}</span>
                </div>
              )}

              <div className="modal-footer">
                <button
                  type="button"
                  className="cancel-btn"
                  onClick={() => setIsKnowledgeModalOpen(false)}
                >
                  Fechar
                </button>
                <button
                  type="submit"
                  className="submit-btn"
                  disabled={loadingIngest}
                >
                  {loadingIngest
                    ? 'Gerando embeddings...'
                    : `Salvar e Vetorizar (${currentEnv.name})`}
                </button>
              </div>
            </form>
          </div>
        </div>
      )}
    </div>
  )
}
