import { useState, useCallback, useRef, useEffect } from 'react'
import { useQuery, useMutation, useQueryClient } from '@tanstack/react-query'
import { useSearchParams, useNavigate } from 'react-router-dom'
import {
  Search, RefreshCw, ChevronDown, ChevronRight,
  Loader2, AlertCircle, Database, Code2, MoreHorizontal,
  Trash2, Sparkles, FileText, Clock, Calendar, Cpu, X,
  GitBranch, Zap, FlaskConical, ShieldCheck,
} from 'lucide-react'
import { api } from '../api'
import { RichSymbolDetail, CodePeek, Chip, StatusBadges, type PeekTarget } from './intelligenceUi'
import type { CodeIndexFile, SymbolRecord, SymbolReference } from '../types'

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

function FileSymbolRow({
  s, reference, fileId, onPeek, onNavigateToFile,
}: {
  s: SymbolRecord
  reference?: SymbolReference
  fileId: string
  onPeek: (t: PeekTarget) => void
  onNavigateToFile?: (fileId: string) => void
}) {
  const [open, setOpen] = useState(false)
  const refs = reference?.fanIn ?? 0
  return (
    <div className="border border-zinc-800 rounded-lg overflow-hidden">
      <button onClick={() => setOpen(o => !o)} className="w-full flex items-center gap-2 px-2.5 py-1.5 hover:bg-zinc-800/40 transition-colors text-left">
        {open ? <ChevronDown className="w-3 h-3 text-zinc-400 flex-shrink-0" /> : <ChevronRight className="w-3 h-3 text-zinc-600 flex-shrink-0" />}
        <span className="font-mono text-xs text-zinc-200 truncate flex-1 min-w-0">{s.name}</span>
        <StatusBadges s={s} reference={reference} />
        <span className="text-[10px] text-zinc-500 flex-shrink-0">{s.kind}</span>
        {refs > 0 && <span className="text-[10px] text-indigo-400 tabular-nums flex-shrink-0">↙{refs}</span>}
        <span className="text-[10px] text-zinc-600 font-mono flex-shrink-0">:{s.line}</span>
      </button>
      {open && (
        <div className="border-t border-zinc-800 p-3 bg-zinc-950/30">
          <RichSymbolDetail
            record={s}
            reference={reference}
            fileId={fileId}
            onPeek={onPeek}
            onNavigateToFile={onNavigateToFile}
          />
        </div>
      )}
    </div>
  )
}

function FileSymbolList({
  symbols, definedSymbols, fileId, onPeek, onNavigateToFile,
}: {
  symbols: SymbolRecord[]
  definedSymbols?: SymbolReference[]
  fileId: string
  onPeek: (t: PeekTarget) => void
  onNavigateToFile?: (fileId: string) => void
}) {
  if (!symbols.length) return <p className="text-xs text-zinc-500 italic">No symbols extracted.</p>
  const refByName = new Map<string, SymbolReference>()
  for (const r of definedSymbols ?? []) refByName.set(r.name, r)
  return (
    <div className="space-y-1 max-h-[28rem] overflow-y-auto pr-1">
      {symbols.map((s, i) => (
        <FileSymbolRow
          key={i}
          s={s}
          reference={refByName.get(s.name)}
          fileId={fileId}
          onPeek={onPeek}
          onNavigateToFile={onNavigateToFile}
        />
      ))}
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

function FileRow({
  file,
  expanded,
  onToggle,
  projectId,
  rowRef,
  onPeek,
  onNavigateToFile,
}: {
  file: CodeIndexFile
  expanded: boolean
  onToggle: () => void
  projectId: string
  rowRef?: React.Ref<HTMLDivElement>
  onPeek: (t: PeekTarget) => void
  onNavigateToFile?: (fileId: string) => void
}) {
  const profileQuery = useQuery({
    queryKey: ['intelligence-file', projectId, file.id],
    queryFn: () => api.intelligence.getFileProfile(projectId, file.id),
    enabled: expanded,
    staleTime: 60_000,
  })

  const fanIn  = file.fanIn  ?? 0
  const fanOut = file.fanOut ?? 0

  return (
    <div
      ref={rowRef}
      className={`border rounded-lg overflow-hidden transition-colors ${
        expanded ? 'border-zinc-700' : 'border-zinc-800'
      }`}
    >
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
          {file.isEntrypoint && <Chip tone="green" title="entrypoint"><Zap className="w-2.5 h-2.5" />entry</Chip>}
          {file.isTestFile && <Chip tone="sky" title={file.testFramework ?? 'test file'}><FlaskConical className="w-2.5 h-2.5" />test</Chip>}
          {(file.orphanSymbolCount ?? 0) > 0 && <Chip tone="orange" title="public symbols with no references anywhere">{file.orphanSymbolCount} orphan</Chip>}
          {file.score != null && <ScoreBadge score={file.score} />}
          {langBadge(file.language)}
          <span className="text-xs text-zinc-500 tabular-nums">{file.symbols.length} sym</span>
          {fanIn > 0 && (
            <span className="text-[10px] text-indigo-400 tabular-nums flex-shrink-0" title={`${fanIn} files depend on this`}>
              ↙{fanIn}
            </span>
          )}
          {fanOut > 0 && (
            <span className="text-[10px] text-zinc-500 tabular-nums flex-shrink-0" title={`This file depends on ${fanOut} files`}>
              ↗{fanOut}
            </span>
          )}
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

          {/* Diagnostic summary */}
          {file.diagnosticSummary && (
            <div className="mx-4 mt-4 flex items-start gap-2 rounded-lg border border-amber-800/50 bg-amber-950/20 px-3 py-2.5">
              <AlertCircle className="w-4 h-4 text-amber-400 flex-shrink-0 mt-0.5" />
              <p className="text-xs text-amber-300 font-mono">{file.diagnosticSummary}</p>
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
            {(file.domainTags ?? []).length > 0 && (
              <div className="flex items-center gap-1.5 flex-wrap">
                {(file.domainTags ?? []).map(t => (
                  <span key={t} className="text-[10px] px-1.5 py-0.5 rounded bg-zinc-800 text-zinc-400 border border-zinc-700">
                    {t}
                  </span>
                ))}
              </div>
            )}
          </div>

          {/* Insights strip */}
          <div className="px-4 py-2.5 flex flex-wrap items-center gap-1.5 border-b border-zinc-800">
            {file.architecturalRole && <Chip tone="violet" title="architectural role">{file.architecturalRole}</Chip>}
            {file.isEntrypoint && <Chip tone="green"><Zap className="w-2.5 h-2.5" />entrypoint</Chip>}
            {file.isTestFile && <Chip tone="sky"><FlaskConical className="w-2.5 h-2.5" />{file.testFramework ?? 'test'}</Chip>}
            {file.hasValidation && <Chip tone="green"><ShieldCheck className="w-2.5 h-2.5" />validated DTOs</Chip>}
            {file.hasUnusedPublicSymbols && <Chip tone="orange"><Trash2 className="w-2.5 h-2.5" />{file.orphanSymbolCount ?? 0} orphan symbol(s)</Chip>}
            <span className="ml-auto flex items-center gap-1.5">
              <span className="text-[10px] text-indigo-400 tabular-nums" title="files depending on this">↙{fanIn} in</span>
              <span className="text-[10px] text-emerald-400 tabular-nums" title="files this depends on">↗{fanOut} out</span>
            </span>
            <button
              onClick={() => onPeek({ fileId: file.id, title: file.relativePath })}
              className="flex items-center gap-1.5 px-2 py-1 text-[11px] text-indigo-400 hover:text-indigo-300 bg-indigo-500/10 hover:bg-indigo-500/20 border border-indigo-500/25 rounded-lg transition-colors"
            >
              <Code2 className="w-3 h-3" /> View file
            </button>
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
              <FileSymbolList
                symbols={file.symbols}
                definedSymbols={profileQuery.data?.definedSymbols}
                fileId={file.id}
                onPeek={onPeek}
                onNavigateToFile={onNavigateToFile}
              />
            </div>
          </div>

          {/* Dependencies section */}
          {(fanIn > 0 || fanOut > 0 || profileQuery.data) && (
            <div className="border-t border-zinc-800 p-4 space-y-3">
              <h4 className="text-xs font-medium text-zinc-400 flex items-center gap-1.5">
                <GitBranch className="w-3.5 h-3.5 text-violet-400" />
                Dependencies
                {profileQuery.isLoading && (
                  <Loader2 className="w-3 h-3 animate-spin text-zinc-600 ml-1" />
                )}
              </h4>

              {profileQuery.data && (
                <div className="grid grid-cols-2 gap-4">
                  {/* Used by */}
                  <div className="space-y-1">
                    <p className="text-[10px] text-zinc-500 uppercase tracking-wide">
                      Used by {profileQuery.data.definedSymbols.flatMap(s => s.usedBy).length > 0
                        ? `${new Set(profileQuery.data.definedSymbols.flatMap(s => s.usedBy.map(u => u.fileId))).size} files`
                        : '0 files'}
                    </p>
                    {Array.from(
                      new Map(
                        profileQuery.data.definedSymbols
                          .flatMap(s => s.usedBy)
                          .map(u => [u.fileId, u])
                      ).values()
                    ).slice(0, 8).map(site => (
                      <div key={site.fileId} className="flex items-center gap-2 text-xs">
                        <span className="flex-1 font-mono text-zinc-400 truncate">{site.relativePath}</span>
                        {onNavigateToFile && (
                          <button
                            onClick={() => onNavigateToFile(site.fileId)}
                            className="text-indigo-400 hover:text-indigo-300 flex-shrink-0 text-[10px]"
                          >
                            → Go to
                          </button>
                        )}
                      </div>
                    ))}
                  </div>

                  {/* Depends on */}
                  <div className="space-y-1">
                    <p className="text-[10px] text-zinc-500 uppercase tracking-wide">
                      Depends on {profileQuery.data.dependsOn.length} files
                    </p>
                    {profileQuery.data.dependsOn.slice(0, 8).map(dep => (
                      <div key={dep.id} className="flex items-center gap-2 text-xs">
                        <span className="flex-1 font-mono text-zinc-400 truncate">{dep.relativePath}</span>
                        {onNavigateToFile && (
                          <button
                            onClick={() => onNavigateToFile(dep.id)}
                            className="text-indigo-400 hover:text-indigo-300 flex-shrink-0 text-[10px]"
                          >
                            → Go to
                          </button>
                        )}
                      </div>
                    ))}
                  </div>
                </div>
              )}
            </div>
          )}
        </div>
      )}
    </div>
  )
}

export function FilesIndex({
  projectId,
  subProjectId,
  onNavigateToFile,
}: {
  projectId: string
  subProjectId?: string
  onNavigateToFile?: (fileId: string) => void
}) {
  const [searchParams, setSearchParams] = useSearchParams()
  const expandFileId = searchParams.get('expandFile')

  const [search, setSearch] = useState('')
  const [expanded, setExpanded] = useState<string | null>(expandFileId)
  const [peek, setPeek] = useState<PeekTarget | null>(null)
  const [menuOpen, setMenuOpen] = useState(false)
  const menuRef = useRef<HTMLDivElement>(null)
  const expandedRef = useRef<HTMLDivElement>(null)
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
    queryKey: ['codeindex-files', projectId, subProjectId, debouncedSearch],
    queryFn: () => api.codeIndex.listFiles(projectId, debouncedSearch || undefined, subProjectId),
    staleTime: 15_000,
  })

  // React to a "go to file" request whenever the expandFile param changes — NOT just on mount.
  // (When already on the Files tab the component stays mounted, so a mount-only effect never fires.)
  // Wait until the target row is actually in the loaded list before expanding + scrolling, then drop
  // the param so a manual collapse sticks. Other params (tab/sub) are preserved.
  useEffect(() => {
    if (!expandFileId) return
    if (!files?.some(f => f.id === expandFileId)) return
    setExpanded(expandFileId)
    const frame = requestAnimationFrame(() => {
      expandedRef.current?.scrollIntoView({ behavior: 'smooth', block: 'start' })
      setSearchParams(prev => {
        const next = new URLSearchParams(prev)
        next.delete('expandFile')
        return next
      }, { replace: true })
    })
    return () => cancelAnimationFrame(frame)
  }, [expandFileId, files, setSearchParams])

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
              projectId={projectId}
              rowRef={f.id === expandFileId ? expandedRef : undefined}
              onPeek={setPeek}
              onNavigateToFile={onNavigateToFile}
            />
          ))}
        </div>
      )}

      {peek && <CodePeek projectId={projectId} target={peek} onClose={() => setPeek(null)} />}
    </div>
  )
}
