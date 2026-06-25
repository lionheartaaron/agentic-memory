import { useState, useCallback, useRef, useEffect } from 'react'
import { useQuery, useMutation, useQueryClient } from '@tanstack/react-query'
import {
  Search, RefreshCw, ChevronDown, ChevronRight,
  Loader2, AlertCircle, Database, Code2, MoreHorizontal,
  Trash2, Sparkles, FileText, Clock, Calendar, Cpu, X,
} from 'lucide-react'
import { useNavigate } from 'react-router-dom'
import { api } from '../api'
import type { CodeIndexFile, SymbolRecord } from '../types'

function useDebounce<T>(value: T, ms: number): T {
  const [debounced, setDebounced] = useState(value)
  const timerRef = { current: 0 as ReturnType<typeof setTimeout> }
  const set = useCallback((v: T) => {
    clearTimeout(timerRef.current)
    timerRef.current = setTimeout(() => setDebounced(v), ms)
  // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [ms])
  set(value)
  return debounced
}

const LANG_COLORS: Record<string, string> = {
  csharp:     'bg-violet-500/15 text-violet-300 border-violet-500/25',
  typescript: 'bg-blue-500/15 text-blue-300 border-blue-500/25',
  javascript: 'bg-yellow-500/15 text-yellow-300 border-yellow-500/25',
  python:     'bg-green-500/15 text-green-300 border-green-500/25',
  go:         'bg-cyan-500/15 text-cyan-300 border-cyan-500/25',
  rust:       'bg-orange-500/15 text-orange-300 border-orange-500/25',
  unknown:    'bg-zinc-700/40 text-zinc-400 border-zinc-600/25',
}

function langBadge(lang: string) {
  const cls = LANG_COLORS[lang] ?? LANG_COLORS.unknown
  return (
    <span className={`inline-flex items-center px-1.5 py-0.5 rounded text-[10px] font-medium border ${cls}`}>
      {lang}
    </span>
  )
}

function fmtDate(iso: string) {
  return new Date(iso).toLocaleString(undefined, {
    year: 'numeric', month: 'short', day: 'numeric',
    hour: '2-digit', minute: '2-digit',
  })
}

function timeAgo(iso: string) {
  const diff = Date.now() - new Date(iso).getTime()
  if (diff < 60_000) return 'just now'
  if (diff < 3_600_000) return `${Math.floor(diff / 60_000)}m ago`
  if (diff < 86_400_000) return `${Math.floor(diff / 3_600_000)}h ago`
  return `${Math.floor(diff / 86_400_000)}d ago`
}

function SymbolsTable({ symbols }: { symbols: SymbolRecord[] }) {
  if (!symbols.length) return <p className="text-xs text-zinc-500 italic">No symbols extracted.</p>
  return (
    <div className="overflow-x-auto">
      <table className="w-full text-xs">
        <thead>
          <tr className="border-b border-zinc-700/60">
            <th className="text-left py-1.5 pr-4 text-zinc-500 font-medium">Name</th>
            <th className="text-left py-1.5 pr-4 text-zinc-500 font-medium">Kind</th>
            <th className="text-left py-1.5 pr-4 text-zinc-500 font-medium">Type</th>
            <th className="text-left py-1.5 pr-4 text-zinc-500 font-medium">Access</th>
            <th className="text-right py-1.5 text-zinc-500 font-medium">Line</th>
          </tr>
        </thead>
        <tbody>
          {symbols.map((s, i) => (
            <tr key={i} className="border-b border-zinc-800/50 hover:bg-zinc-800/30">
              <td className="py-1.5 pr-4 font-mono text-zinc-200 font-medium">{s.name}</td>
              <td className="py-1.5 pr-4 text-zinc-400">{s.kind}</td>
              <td className="py-1.5 pr-4 text-zinc-500 font-mono truncate max-w-[200px]" title={s.type ?? ''}>
                {s.type ?? '—'}
              </td>
              <td className="py-1.5 pr-4">
                <span className={`text-[10px] font-medium px-1.5 py-0.5 rounded ${
                  s.accessibility === 'public'
                    ? 'bg-green-500/10 text-green-400'
                    : s.accessibility === 'private'
                    ? 'bg-zinc-700/60 text-zinc-500'
                    : 'bg-amber-500/10 text-amber-400'
                }`}>
                  {s.accessibility}
                </span>
              </td>
              <td className="py-1.5 text-right text-zinc-500 font-mono">{s.line}</td>
            </tr>
          ))}
        </tbody>
      </table>
    </div>
  )
}

function ScoreBadge({ score }: { score: number }) {
  const pct = Math.round(score * 100)
  const color = pct >= 70
    ? 'bg-green-500/15 text-green-400 border-green-500/25'
    : pct >= 50
    ? 'bg-amber-500/15 text-amber-400 border-amber-500/25'
    : 'bg-zinc-700/40 text-zinc-400 border-zinc-600/25'
  return (
    <span className={`inline-flex items-center px-1.5 py-0.5 rounded text-[10px] font-mono font-medium border ${color}`}>
      {pct}%
    </span>
  )
}

function FileRow({ file, expanded, onToggle }: {
  file: CodeIndexFile
  expanded: boolean
  onToggle: () => void
}) {
  return (
    <div className={`border rounded-lg overflow-hidden transition-colors ${
      expanded ? 'border-zinc-700' : 'border-zinc-800'
    }`}>
      {/* Collapsed header */}
      <button
        onClick={onToggle}
        className="w-full flex items-center gap-3 px-4 py-3 hover:bg-zinc-800/40 transition-colors text-left"
      >
        {expanded
          ? <ChevronDown className="w-3.5 h-3.5 text-zinc-400 flex-shrink-0" />
          : <ChevronRight className="w-3.5 h-3.5 text-zinc-600 flex-shrink-0" />}

        <span className="flex-1 min-w-0">
          <span className="font-mono text-sm text-zinc-200">{file.relativePath}</span>
          {!expanded && file.llmSummary && (
            <span className="block text-xs text-zinc-500 mt-0.5 truncate">{file.llmSummary}</span>
          )}
        </span>

        <div className="flex items-center gap-2 flex-shrink-0">
          {file.score != null && <ScoreBadge score={file.score} />}
          {langBadge(file.language)}
          <span className="text-xs text-zinc-500 tabular-nums">{file.symbols.length} sym</span>
          <span className="text-xs text-zinc-600">{timeAgo(file.indexedAt)}</span>
          {file.isStale && (
            <span className="w-2 h-2 rounded-full bg-amber-400 flex-shrink-0" title="Stale — queued for re-index" />
          )}
          {file.ingestionError && (
            <span title={file.ingestionError} className="flex-shrink-0">
              <AlertCircle className="w-3.5 h-3.5 text-red-400" />
            </span>
          )}
        </div>
      </button>

      {/* Expanded detail */}
      {expanded && (
        <div className="border-t border-zinc-800">

          {/* Error banner */}
          {file.ingestionError && (
            <div className="mx-4 mt-4 flex items-start gap-2 rounded-lg border border-red-800/50 bg-red-950/30 px-3 py-2.5">
              <AlertCircle className="w-4 h-4 text-red-400 flex-shrink-0 mt-0.5" />
              <p className="text-xs text-red-400 font-mono">{file.ingestionError}</p>
            </div>
          )}

          {/* LLM summary */}
          {file.llmSummary && (
            <div className="px-4 pt-4 pb-3">
              <div className="flex items-center gap-1.5 text-xs font-medium text-zinc-400 mb-1.5">
                <Sparkles className="w-3.5 h-3.5 text-indigo-400" />
                Summary
              </div>
              <p className="text-sm text-zinc-300 leading-relaxed">{file.llmSummary}</p>
            </div>
          )}

          {/* Metadata strip */}
          <div className="px-4 py-2.5 bg-zinc-900/50 border-y border-zinc-800 flex flex-wrap gap-x-6 gap-y-1.5">
            <div className="flex items-center gap-1.5 text-xs text-zinc-500">
              <FileText className="w-3 h-3" />
              <span className="font-mono text-zinc-400 break-all">{file.filePath}</span>
            </div>
            <div className="flex items-center gap-1.5 text-xs text-zinc-500">
              <Clock className="w-3 h-3" />
              Indexed <span className="text-zinc-400 ml-1">{fmtDate(file.indexedAt)}</span>
            </div>
            <div className="flex items-center gap-1.5 text-xs text-zinc-500">
              <Calendar className="w-3 h-3" />
              Modified <span className="text-zinc-400 ml-1">{fmtDate(file.fileModifiedAt)}</span>
            </div>
            <div className="flex items-center gap-1.5 text-xs text-zinc-500">
              <Cpu className="w-3 h-3" />
              <span className="text-zinc-400">{file.providerType || '—'}</span>
            </div>
          </div>

          {/* Context + Symbols */}
          <div className="grid grid-cols-2 divide-x divide-zinc-800">
            <div className="p-4 space-y-2 min-w-0">
              <h4 className="text-xs font-medium text-zinc-400 flex items-center gap-1.5">
                <Code2 className="w-3.5 h-3.5" /> Extracted Context
              </h4>
              <pre className="text-xs text-zinc-300 font-mono whitespace-pre-wrap leading-relaxed max-h-96 overflow-y-auto">
                {file.extractedContext || '(empty)'}
              </pre>
            </div>
            <div className="p-4 space-y-2 min-w-0">
              <h4 className="text-xs font-medium text-zinc-400 flex items-center gap-1.5">
                <Database className="w-3.5 h-3.5" />
                Symbols
                {file.symbols.length > 0 && (
                  <span className="ml-auto text-zinc-600 font-normal">{file.symbols.length}</span>
                )}
              </h4>
              <SymbolsTable symbols={file.symbols} />
            </div>
          </div>
        </div>
      )}
    </div>
  )
}

export function FilesIndex({ projectId }: { projectId: string }) {
  const [search, setSearch] = useState('')
  const [expanded, setExpanded] = useState<string | null>(null)
  const [menuOpen, setMenuOpen] = useState(false)
  const menuRef = useRef<HTMLDivElement>(null)
  const queryClient = useQueryClient()
  const navigate = useNavigate()
  const debouncedSearch = useDebounce(search, 300)

  const { data: status } = useQuery({
    queryKey: ['status'],
    queryFn: api.systemStatus,
    staleTime: 60_000,
    retry: false,
  })
  const semanticAvailable = status?.embeddings?.available ?? false

  const { data: files, isLoading, error } = useQuery({
    queryKey: ['codeindex-files', projectId, debouncedSearch],
    queryFn: () => api.codeIndex.listFiles(projectId, debouncedSearch || undefined),
    staleTime: 15_000,
  })

  const reindexMutation = useMutation({
    mutationFn: () => api.codeIndex.reindex(projectId),
    onSuccess: () => {
      queryClient.invalidateQueries({ queryKey: ['codeindex-files', projectId] })
      navigate('/worker')
    },
  })

  const forceReindexMutation = useMutation({
    mutationFn: () => api.codeIndex.forceReindexAll(projectId),
    onSuccess: () => {
      setMenuOpen(false)
      queryClient.invalidateQueries({ queryKey: ['codeindex-files', projectId] })
      navigate('/worker')
    },
  })

  useEffect(() => {
    if (!menuOpen) return
    const handler = (e: MouseEvent) => {
      if (menuRef.current && !menuRef.current.contains(e.target as Node))
        setMenuOpen(false)
    }
    document.addEventListener('mousedown', handler)
    return () => document.removeEventListener('mousedown', handler)
  }, [menuOpen])

  const toggle = (id: string) => setExpanded(prev => prev === id ? null : id)

  const resultSymbols = files?.reduce((s, f) => s + f.symbols.length, 0) ?? 0

  return (
    <div className="space-y-4">
      {/* Toolbar */}
      <div className="flex items-center gap-3">
        <div className="flex-1 relative">
          <Search className="absolute left-3 top-1/2 -translate-y-1/2 w-3.5 h-3.5 text-zinc-500 pointer-events-none" />
          <input
            type="text"
            value={search}
            onChange={e => setSearch(e.target.value)}
            placeholder={semanticAvailable ? 'Semantic search files…' : 'Search files…'}
            className="w-full bg-zinc-800 border border-zinc-700 rounded-lg pl-9 pr-28 py-2 text-sm text-zinc-100 placeholder-zinc-500 focus:outline-none focus:ring-2 focus:ring-indigo-500 focus:border-transparent"
          />
          <div className="absolute right-2 top-1/2 -translate-y-1/2 flex items-center gap-1.5">
            {debouncedSearch && (
              <span className={`text-[10px] font-medium px-1.5 py-0.5 rounded border ${
                semanticAvailable
                  ? 'bg-indigo-500/15 text-indigo-400 border-indigo-500/25'
                  : 'bg-zinc-700/60 text-zinc-400 border-zinc-600/25'
              }`}>
                {semanticAvailable ? 'semantic' : 'text'}
              </span>
            )}
            {search && (
              <button
                onClick={() => setSearch('')}
                className="p-0.5 rounded text-zinc-500 hover:text-zinc-300 hover:bg-zinc-700 transition-colors"
              >
                <X className="w-3.5 h-3.5" />
              </button>
            )}
          </div>
        </div>

        <button
          onClick={() => reindexMutation.mutate()}
          disabled={reindexMutation.isPending}
          className="flex items-center gap-2 px-3 py-2 bg-zinc-700 hover:bg-zinc-600 disabled:opacity-40 disabled:cursor-not-allowed text-zinc-200 text-sm font-medium rounded-lg transition-colors"
        >
          {reindexMutation.isPending
            ? <Loader2 className="w-3.5 h-3.5 animate-spin" />
            : <RefreshCw className="w-3.5 h-3.5" />}
          Re-index
        </button>

        <div className="relative" ref={menuRef}>
          <button
            onClick={() => setMenuOpen(o => !o)}
            className="p-2 bg-zinc-700 hover:bg-zinc-600 text-zinc-200 rounded-lg transition-colors"
          >
            <MoreHorizontal className="w-4 h-4" />
          </button>
          {menuOpen && (
            <div className="absolute right-0 top-full mt-1 z-50 min-w-[190px] bg-zinc-900 border border-zinc-700 rounded-lg shadow-xl py-1">
              <button
                onClick={() => forceReindexMutation.mutate()}
                disabled={forceReindexMutation.isPending}
                className="w-full flex items-center gap-2 px-3 py-2 text-sm text-red-400 hover:bg-zinc-800 disabled:opacity-40 disabled:cursor-not-allowed text-left transition-colors"
              >
                {forceReindexMutation.isPending
                  ? <Loader2 className="w-3.5 h-3.5 animate-spin flex-shrink-0" />
                  : <Trash2 className="w-3.5 h-3.5 flex-shrink-0" />}
                Clear &amp; re-index all
              </button>
            </div>
          )}
        </div>

        {files && (
          <span className="text-xs text-zinc-500 flex-shrink-0 tabular-nums whitespace-nowrap">
            {debouncedSearch
              ? <>{files.length} result{files.length !== 1 ? 's' : ''}{resultSymbols > 0 && ` · ${resultSymbols} sym`}</>
              : <>{files.length} file{files.length !== 1 ? 's' : ''}{resultSymbols > 0 && ` · ${resultSymbols} sym`}</>
            }
          </span>
        )}
      </div>

      {/* File list */}
      {isLoading ? (
        <div className="flex items-center justify-center py-16">
          <Loader2 className="w-5 h-5 animate-spin text-zinc-500" />
        </div>
      ) : error ? (
        <div className="rounded-lg border border-red-800/50 bg-red-950/30 px-4 py-3 text-sm text-red-400">
          Failed to load indexed files.
        </div>
      ) : !files?.length ? (
        <div className="py-16 text-center space-y-2">
          <Database className="w-8 h-8 text-zinc-700 mx-auto" />
          <p className="text-zinc-500 text-sm">
            {search ? 'No matching files.' : 'No files indexed yet. Click Re-index to start.'}
          </p>
        </div>
      ) : (
        <div className="space-y-1.5">
          {files.map(f => (
            <FileRow
              key={f.id}
              file={f}
              expanded={expanded === f.id}
              onToggle={() => toggle(f.id)}
            />
          ))}
        </div>
      )}
    </div>
  )
}
