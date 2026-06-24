import { useState, useRef, useEffect, useCallback } from 'react'
import { useQuery } from '@tanstack/react-query'
import { AlertCircle, Trash2, StopCircle, Sparkles, ArrowUp } from 'lucide-react'
import { api } from '../api'

interface Message {
  role: 'user' | 'assistant'
  content: string
  error?: boolean
}

function TypingDots() {
  return (
    <span className="inline-flex items-center gap-[3px] px-0.5 py-[3px]">
      {[0, 160, 320].map((delay, i) => (
        <span
          key={i}
          className="w-[7px] h-[7px] rounded-full bg-zinc-400 animate-bounce"
          style={{ animationDelay: `${delay}ms`, animationDuration: '1.1s' }}
        />
      ))}
    </span>
  )
}

function UserBubble({ content }: { content: string }) {
  return (
    <div className="flex justify-end pr-4 pl-16">
      <div
        className="bg-indigo-500 text-white text-[15px] leading-snug px-4 py-[10px] whitespace-pre-wrap break-words select-text shadow-sm"
        style={{
          borderRadius: '20px 20px 4px 20px',
          maxWidth: '340px',
        }}
      >
        {content}
      </div>
    </div>
  )
}

function AssistantBubble({
  content,
  streaming,
  error,
  showAvatar,
}: {
  content: string
  streaming?: boolean
  error?: boolean
  showAvatar?: boolean
}) {
  const isEmpty = !content && streaming

  return (
    <div className="flex items-end gap-2 pl-4 pr-16">
      <div className="w-7 h-7 flex-shrink-0">
        {showAvatar && (
          <div className="w-7 h-7 rounded-full bg-gradient-to-br from-indigo-500 to-purple-600 flex items-center justify-center shadow-md shadow-indigo-500/20">
            <Sparkles className="w-[13px] h-[13px] text-white" />
          </div>
        )}
      </div>
      <div
        className={`text-[15px] leading-snug px-4 py-[10px] whitespace-pre-wrap break-words select-text shadow-sm ${
          error
            ? 'bg-red-950/70 border border-red-800/50 text-red-300'
            : 'bg-zinc-800 text-zinc-100'
        }`}
        style={{
          borderRadius: '20px 20px 20px 4px',
          maxWidth: '340px',
        }}
      >
        {isEmpty ? (
          <TypingDots />
        ) : (
          <>
            {content}
            {streaming && (
              <span className="inline-block w-[2px] h-[1em] bg-zinc-400 ml-0.5 animate-pulse align-text-bottom rounded-full" />
            )}
          </>
        )}
      </div>
    </div>
  )
}

export default function Chat() {
  const [messages, setMessages] = useState<Message[]>([])
  const [input, setInput] = useState('')
  const [streaming, setStreaming] = useState(false)
  const bottomRef = useRef<HTMLDivElement>(null)
  const inputRef = useRef<HTMLTextAreaElement>(null)
  const abortRef = useRef<AbortController | null>(null)

  const { data: status, isLoading: statusLoading } = useQuery({
    queryKey: ['generate-status'],
    queryFn: api.generateStatus,
    refetchInterval: 10_000,
    retry: false,
  })

  const modelAvailable = status?.available === true

  useEffect(() => {
    bottomRef.current?.scrollIntoView({ behavior: 'smooth' })
  }, [messages])

  const stopGeneration = useCallback(() => {
    abortRef.current?.abort()
  }, [])

  const clearChat = useCallback(() => {
    if (streaming) stopGeneration()
    setMessages([])
  }, [streaming, stopGeneration])

  const send = useCallback(async () => {
    const text = input.trim()
    if (!text || streaming || !modelAvailable) return

    setInput('')
    if (inputRef.current) inputRef.current.style.height = 'auto'

    setMessages(prev => [...prev, { role: 'user', content: text }])
    setStreaming(true)
    setMessages(prev => [...prev, { role: 'assistant', content: '' }])

    abortRef.current = new AbortController()

    try {
      const res = await fetch('/api/generate/stream', {
        method: 'POST',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({ userPrompt: text }),
        signal: abortRef.current.signal,
      })

      if (!res.ok) throw new Error(`Server returned ${res.status}`)

      const reader = res.body!.getReader()
      const decoder = new TextDecoder()
      let buffer = ''

      while (true) {
        const { done, value } = await reader.read()
        if (done) break

        buffer += decoder.decode(value, { stream: true })
        const lines = buffer.split('\n')
        buffer = lines.pop() ?? ''

        for (const line of lines) {
          if (!line.startsWith('data: ')) continue
          const data = line.slice(6).trim()
          if (data === '[DONE]') break

          try {
            const parsed = JSON.parse(data) as { token?: string; error?: string }
            if (parsed.error) throw new Error(parsed.error)
            if (parsed.token) {
              setMessages(prev => {
                const next = [...prev]
                const last = next[next.length - 1]
                next[next.length - 1] = { ...last, content: last.content + parsed.token }
                return next
              })
            }
          } catch {
            // Malformed SSE line — skip
          }
        }
      }
    } catch (err) {
      const isAbort = (err as Error).name === 'AbortError'
      if (!isAbort) {
        setMessages(prev => {
          const next = [...prev]
          next[next.length - 1] = {
            role: 'assistant',
            content: `Error: ${(err as Error).message}`,
            error: true,
          }
          return next
        })
      }
    } finally {
      setStreaming(false)
      abortRef.current = null
      setTimeout(() => inputRef.current?.focus(), 0)
    }
  }, [input, streaming, modelAvailable])

  const handleKeyDown = (e: React.KeyboardEvent<HTMLTextAreaElement>) => {
    if (e.key === 'Enter' && !e.shiftKey) {
      e.preventDefault()
      send()
    }
  }

  const handleInput = (e: React.ChangeEvent<HTMLTextAreaElement>) => {
    setInput(e.target.value)
    const el = e.target
    el.style.height = 'auto'
    el.style.height = `${Math.min(el.scrollHeight, 140)}px`
  }

  const canSend = !!input.trim() && modelAvailable && !streaming

  return (
    <div className="flex flex-col h-full bg-zinc-950">
      {/* Header */}
      <div className="flex-shrink-0 border-b border-zinc-800/60 px-5 pt-4 pb-3">
        <div className="flex items-center justify-between">
          <div className="flex items-center gap-3">
            <div className="relative">
              <div className="w-11 h-11 rounded-full bg-gradient-to-br from-indigo-500 to-purple-600 flex items-center justify-center shadow-lg shadow-indigo-500/25">
                <Sparkles className="w-5 h-5 text-white" />
              </div>
              {modelAvailable && (
                <span className="absolute bottom-0 right-0 w-3 h-3 rounded-full bg-green-400 border-2 border-zinc-950" />
              )}
            </div>
            <div>
              <h1 className="text-[15px] font-semibold text-zinc-100 leading-tight">Phi-4-mini</h1>
              <div className="flex items-center gap-1.5 mt-0.5">
                {statusLoading ? (
                  <span className="text-xs text-zinc-600">Checking…</span>
                ) : modelAvailable ? (
                  <span className="text-xs text-zinc-500">Online · local inference</span>
                ) : (
                  <span className="text-xs text-red-400 flex items-center gap-1">
                    <AlertCircle className="w-3 h-3" />
                    Unavailable
                  </span>
                )}
              </div>
            </div>
          </div>

          {messages.length > 0 && (
            <button
              onClick={clearChat}
              className="flex items-center gap-1.5 px-3 py-1.5 text-xs text-zinc-500 hover:text-zinc-300 hover:bg-zinc-800/70 rounded-xl transition-colors"
            >
              <Trash2 className="w-3.5 h-3.5" />
              Clear
            </button>
          )}
        </div>
      </div>

      {/* Messages */}
      <div className="flex-1 overflow-y-auto py-5">
        {messages.length === 0 ? (
          <div className="flex flex-col items-center justify-center h-full gap-6 text-center px-8">
            <div className="w-20 h-20 rounded-3xl bg-gradient-to-br from-indigo-500 to-purple-600 flex items-center justify-center shadow-2xl shadow-indigo-500/30">
              <Sparkles className="w-9 h-9 text-white" />
            </div>
            <div>
              <div className="text-lg font-semibold text-zinc-200">Phi-4-mini-instruct</div>
              <div className="text-sm text-zinc-500 mt-1.5 leading-relaxed max-w-xs">
                {modelAvailable
                  ? 'Local AI on your device. Ask me anything.'
                  : 'Enable Generation in appsettings.json to start chatting.'}
              </div>
            </div>
            {modelAvailable && (
              <div className="flex flex-col gap-2 w-full max-w-[260px]">
                {[
                  'Summarize ONNX Runtime GenAI',
                  'Write a haiku about memory',
                  'What is 17 × 23?',
                ].map(s => (
                  <button
                    key={s}
                    onClick={() => { setInput(s); inputRef.current?.focus() }}
                    className="px-4 py-2.5 text-sm bg-zinc-800/70 hover:bg-zinc-800 text-zinc-400 hover:text-zinc-200 rounded-2xl transition-colors border border-zinc-700/40 text-left leading-snug"
                  >
                    {s}
                  </button>
                ))}
              </div>
            )}
          </div>
        ) : (
          <div>
            {messages.map((msg, i) => {
              const prevMsg = i > 0 ? messages[i - 1] : null
              const nextMsg = messages[i + 1]
              const isNewGroup = !prevMsg || prevMsg.role !== msg.role
              const isGroupEnd = !nextMsg || nextMsg.role !== msg.role
              const gapClass = isNewGroup && i > 0 ? 'mt-4' : 'mt-[3px]'

              return (
                <div key={i} className={gapClass}>
                  {msg.role === 'user' ? (
                    <UserBubble content={msg.content} />
                  ) : (
                    <AssistantBubble
                      content={msg.content}
                      streaming={streaming && i === messages.length - 1}
                      error={msg.error}
                      showAvatar={isGroupEnd}
                    />
                  )}
                </div>
              )
            })}
          </div>
        )}
        <div ref={bottomRef} className="h-3" />
      </div>

      {/* Input bar */}
      <div className="flex-shrink-0 border-t border-zinc-800/60 px-4 py-3">
        <div className="flex items-end gap-2.5">
          {/* Pill textarea */}
          <div className="flex-1 flex items-end bg-zinc-900 border border-zinc-700/60 rounded-[24px] px-4 py-[10px] focus-within:border-indigo-500/50 transition-colors">
            <textarea
              ref={inputRef}
              value={input}
              onChange={handleInput}
              onKeyDown={handleKeyDown}
              placeholder={modelAvailable ? 'Message…' : 'Model unavailable'}
              disabled={!modelAvailable || streaming}
              rows={1}
              className="w-full bg-transparent text-[15px] text-zinc-100 placeholder-zinc-600 resize-none focus:outline-none disabled:opacity-40 disabled:cursor-not-allowed leading-relaxed"
              style={{ maxHeight: '140px', minHeight: '24px' }}
            />
          </div>

          {/* Send / Stop */}
          {streaming ? (
            <button
              onClick={stopGeneration}
              className="flex-shrink-0 w-11 h-11 flex items-center justify-center rounded-full bg-red-500 hover:bg-red-400 active:scale-95 text-white transition-all shadow-lg"
              title="Stop generation"
            >
              <StopCircle className="w-[18px] h-[18px]" />
            </button>
          ) : (
            <button
              onClick={send}
              disabled={!canSend}
              className="flex-shrink-0 w-11 h-11 flex items-center justify-center rounded-full bg-indigo-500 hover:bg-indigo-400 active:scale-95 text-white transition-all shadow-lg shadow-indigo-500/30 disabled:opacity-25 disabled:cursor-not-allowed disabled:shadow-none disabled:bg-zinc-700"
              title="Send message"
            >
              <ArrowUp className="w-[18px] h-[18px]" />
            </button>
          )}
        </div>
        <p className="text-center text-[11px] text-zinc-700 mt-2 tracking-wide">
          int4 · CPU · local
        </p>
      </div>
    </div>
  )
}
