import { useState, useEffect, useCallback } from 'react'
import { useQuery, keepPreviousData } from '@tanstack/react-query'
import {
  Search, ChevronDown, ChevronRight, Loader2,
  X, AlertCircle, Braces,
} from 'lucide-react'
import { api } from '../api'
import { RichSymbolDetail, CodePeek, StatusBadges, type PeekTarget } from './intelligenceUi'
import type { SymbolReference } from '../types'

function useDebounce<T>(value: T, ms: number): T {
  const [d, setD] = useState(value)
  const timer = { current: 0 as ReturnType<typeof setTimeout> }
  const schedule = useCallback((v: T) => {
    clearTimeout(timer.current)
    timer.current = setTimeout(() => setD(v), ms)
  // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [ms])
  schedule(value)
  return d
}

const KIND_OPTIONS = [
  'Method', 'Property', 'Field', 'Class', 'Interface',
  'Enum', 'Struct', 'Function', 'Constructor', 'Variable', 'TypeAlias',
]

const ACCESS_COLOR: Record<string, string> = {
  public:    'bg-green-500/10 text-green-400',
  exported:  'bg-green-500/10 text-green-400',
  internal:  'bg-amber-500/10 text-amber-400',
  protected: 'bg-sky-500/10 text-sky-400',
  private:   'bg-zinc-700/60 text-zinc-500',
}

function FanInBadge({ n }: { n: number }) {
  const cls = n >= 10
    ? 'bg-red-500/15 text-red-400 border-red-500/25'
    : n >= 5
    ? 'bg-amber-500/15 text-amber-400 border-amber-500/25'
    : n >= 1
    ? 'bg-indigo-500/15 text-indigo-400 border-indigo-500/25'
    : 'bg-zinc-800 text-zinc-600 border-zinc-700'
  return (
    <span className={`inline-flex items-center px-1.5 py-0.5 rounded text-[10px] font-mono font-medium border ${cls}`}>
      ↙{n}
    </span>
  )
}

function SymbolRow({
  sym,
  expanded,
  onToggle,
  projectId,
  onPeek,
  onNavigateToFile,
}: {
  sym: SymbolReference
  expanded: boolean
  onToggle: () => void
  projectId: string
  onPeek: (t: PeekTarget) => void
  onNavigateToFile?: (fileId: string) => void
}) {
  // On expand, fetch the file profile to recover the full structured symbol shape
  // (signature, docs, contracts, thrown) — the symbol-search endpoint only carries the graph.
  const profile = useQuery({
    queryKey: ['intelligence-file', projectId, sym.definedInFileId],
    queryFn: () => api.intelligence.getFileProfile(projectId, sym.definedInFileId),
    enabled: expanded,
    staleTime: 60_000,
  })
  const record = profile.data?.file.symbols.find(s => s.name === sym.name)

  return (
    <div className={`border rounded-lg overflow-hidden ${expanded ? 'border-zinc-700' : 'border-zinc-800'}`}>
      <button
        onClick={onToggle}
        className="w-full flex items-center gap-3 px-4 py-3 hover:bg-zinc-800/40 transition-colors text-left"
      >
        {expanded
          ? <ChevronDown className="w-3.5 h-3.5 text-zinc-400 flex-shrink-0" />
          : <ChevronRight className="w-3.5 h-3.5 text-zinc-600 flex-shrink-0" />}

        <span className="flex-1 min-w-0">
          <span className="font-mono text-sm text-zinc-200">{sym.name}</span>
          {!expanded && (
            <span className="block text-xs text-zinc-500 mt-0.5 truncate font-mono">{sym.definedInRelativePath}:{sym.definedAtLine}</span>
          )}
        </span>

        <div className="flex items-center gap-2 flex-shrink-0">
          <StatusBadges reference={sym} />
          <span className="text-xs text-zinc-500">{sym.kind}</span>
          <span className={`text-[10px] px-1.5 py-0.5 rounded font-medium ${ACCESS_COLOR[sym.accessibility] ?? ACCESS_COLOR.private}`}>
            {sym.accessibility}
          </span>
          <FanInBadge n={sym.fanIn} />
        </div>
      </button>

      {expanded && (
        <div className="border-t border-zinc-800 p-4">
          <div className="mb-3 flex items-center justify-between gap-2">
            <span className="text-xs text-zinc-500 font-mono truncate">
              {sym.definedInRelativePath}<span className="text-zinc-600">:{sym.definedAtLine}</span>
            </span>
            {onNavigateToFile && (
              <button onClick={() => onNavigateToFile(sym.definedInFileId)} className="flex-shrink-0 text-[10px] text-indigo-400 hover:text-indigo-300 transition-colors">
                → Go to file
              </button>
            )}
          </div>
          {profile.isLoading && !record ? (
            <div className="flex items-center gap-2 text-xs text-zinc-500"><Loader2 className="w-3.5 h-3.5 animate-spin" />Loading symbol detail…</div>
          ) : (
            <RichSymbolDetail
              record={record}
              reference={sym}
              fileId={sym.definedInFileId}
              onPeek={onPeek}
              onNavigateToFile={onNavigateToFile}
            />
          )}
        </div>
      )}
    </div>
  )
}

export function SymbolsIndex({
  projectId,
  subProjectId,
  onNavigateToFile,
}: {
  projectId: string
  subProjectId?: string
  onNavigateToFile?: (fileId: string) => void
}) {
  const [search, setSearch]         = useState('')
  const [kind, setKind]             = useState('')
  const [publicOnly, setPublicOnly] = useState(false)
  const [minRefs, setMinRefs]       = useState(0)
  const [limit, setLimit]           = useState(50)
  const [expanded, setExpanded]     = useState<string | null>(null)
  const [peek, setPeek]             = useState<PeekTarget | null>(null)

  const debouncedSearch = useDebounce(search, 300)

  // Reset limit when filters change
  useEffect(() => {
    setLimit(50)
    setExpanded(null)
  }, [debouncedSearch, kind, publicOnly, minRefs, projectId, subProjectId])

  const { data, isLoading, isFetching, error } = useQuery({
    queryKey: ['symbols', projectId, subProjectId, debouncedSearch, kind, publicOnly, minRefs, limit],
    queryFn: () => api.intelligence.listSymbols(projectId, {
      q: debouncedSearch || undefined,
      kind: kind || undefined,
      publicOnly: publicOnly || undefined,
      minFanIn: minRefs > 0 ? minRefs : undefined,
      subProjectId,
      limit,
    }),
    staleTime: 30_000,
    placeholderData: keepPreviousData,
  })

  const symbols = data?.symbols ?? []
  const total   = data?.total   ?? 0
  const hasMore = symbols.length < total

  const toggle = (id: string) => setExpanded(prev => prev === id ? null : id)

  return (
    <div className="space-y-4">
      {/* Toolbar */}
      <div className="flex flex-wrap items-center gap-3">
        {/* Search */}
        <div className="flex-1 min-w-[200px] relative">
          <Search className="absolute left-3 top-1/2 -translate-y-1/2 w-3.5 h-3.5 text-zinc-500 pointer-events-none" />
          <input
            type="text"
            value={search}
            onChange={e => setSearch(e.target.value)}
            placeholder="Search symbols…"
            className="w-full bg-zinc-800 border border-zinc-700 rounded-lg pl-9 pr-8 py-2 text-sm text-zinc-100 placeholder-zinc-500 focus:outline-none focus:ring-2 focus:ring-indigo-500 focus:border-transparent"
          />
          {search && (
            <button
              onClick={() => setSearch('')}
              className="absolute right-2 top-1/2 -translate-y-1/2 p-0.5 text-zinc-500 hover:text-zinc-300 hover:bg-zinc-700 rounded transition-colors"
            >
              <X className="w-3.5 h-3.5" />
            </button>
          )}
        </div>

        {/* Kind filter */}
        <select
          value={kind}
          onChange={e => setKind(e.target.value)}
          className="bg-zinc-800 border border-zinc-700 rounded-lg px-3 py-2 text-sm text-zinc-300 focus:outline-none focus:ring-2 focus:ring-indigo-500 focus:border-transparent"
        >
          <option value="">All kinds</option>
          {KIND_OPTIONS.map(k => <option key={k} value={k}>{k}</option>)}
        </select>

        {/* Min refs */}
        <div className="flex items-center gap-1.5">
          <span className="text-xs text-zinc-500">Min refs</span>
          <select
            value={minRefs}
            onChange={e => setMinRefs(Number(e.target.value))}
            className="bg-zinc-800 border border-zinc-700 rounded-lg px-2 py-2 text-sm text-zinc-300 focus:outline-none focus:ring-2 focus:ring-indigo-500"
          >
            <option value={0}>Any</option>
            <option value={1}>≥1</option>
            <option value={3}>≥3</option>
            <option value={5}>≥5</option>
            <option value={10}>≥10</option>
          </select>
        </div>

        {/* Public only toggle */}
        <label className="flex items-center gap-1.5 cursor-pointer select-none">
          <div
            onClick={() => setPublicOnly(p => !p)}
            className={`w-8 h-4 rounded-full transition-colors ${publicOnly ? 'bg-indigo-500' : 'bg-zinc-700'}`}
          >
            <div className={`w-3 h-3 bg-white rounded-full m-0.5 transition-transform ${publicOnly ? 'translate-x-4' : ''}`} />
          </div>
          <span className="text-xs text-zinc-400">Public only</span>
        </label>

        {/* Stats */}
        <span className="text-xs text-zinc-500 tabular-nums whitespace-nowrap ml-auto">
          {isLoading ? '…' : `${symbols.length} of ${total.toLocaleString()}`}
          {isFetching && !isLoading && (
            <Loader2 className="w-3 h-3 animate-spin text-zinc-600 inline ml-1.5 align-middle" />
          )}
        </span>
      </div>

      {/* Content */}
      {isLoading ? (
        <div className="flex items-center justify-center py-16">
          <Loader2 className="w-5 h-5 animate-spin text-zinc-500" />
        </div>
      ) : error ? (
        <div className="rounded-lg border border-red-800/50 bg-red-950/30 px-4 py-3 flex items-center gap-2 text-sm text-red-400">
          <AlertCircle className="w-4 h-4 flex-shrink-0" />
          Failed to load symbols. The reference index may not be built yet.
        </div>
      ) : symbols.length === 0 ? (
        <div className="py-16 text-center space-y-3">
          <Braces className="w-8 h-8 text-zinc-700 mx-auto" />
          <div>
            <p className="text-zinc-500 text-sm">
              {(debouncedSearch || kind || publicOnly || minRefs > 0)
                ? 'No symbols match your filters.'
                : 'No symbol references indexed yet.'}
            </p>
            {!debouncedSearch && !kind && !publicOnly && minRefs === 0 && (
              <p className="text-zinc-600 text-xs mt-1">
                The reference worker indexes symbols after files are ingested.
              </p>
            )}
          </div>
          {(debouncedSearch || kind || publicOnly || minRefs > 0) && (
            <button
              onClick={() => { setSearch(''); setKind(''); setPublicOnly(false); setMinRefs(0) }}
              className="text-xs text-indigo-400 hover:text-indigo-300 transition-colors"
            >
              Clear filters
            </button>
          )}
        </div>
      ) : (
        <div className="space-y-1.5">
          {symbols.map(sym => (
            <SymbolRow
              key={sym.id}
              sym={sym}
              expanded={expanded === sym.id}
              onToggle={() => toggle(sym.id)}
              projectId={projectId}
              onPeek={setPeek}
              onNavigateToFile={onNavigateToFile}
            />
          ))}

          {/* Load more */}
          {hasMore && (
            <div className="pt-2 text-center">
              <button
                onClick={() => setLimit(l => l + 50)}
                disabled={isFetching}
                className="px-4 py-2 text-sm text-zinc-300 bg-zinc-800 hover:bg-zinc-700 border border-zinc-700 rounded-lg transition-colors disabled:opacity-40 disabled:cursor-not-allowed"
              >
                {isFetching
                  ? <span className="flex items-center gap-2"><Loader2 className="w-3.5 h-3.5 animate-spin" />Loading…</span>
                  : `Load more (${total - symbols.length} remaining)`}
              </button>
            </div>
          )}
        </div>
      )}

      {peek && <CodePeek projectId={projectId} target={peek} onClose={() => setPeek(null)} />}
    </div>
  )
}
