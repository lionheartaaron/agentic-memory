import { useState, useCallback } from 'react'
import { useQuery, keepPreviousData } from '@tanstack/react-query'
import {
  Sparkles, Search, X, Loader2, AlertCircle, FileCode2,
  Boxes, Route, Database, Network, Settings2, ShieldAlert, Package,
  FlaskConical, Trash2, Layers, Zap, ArrowDownLeft, ArrowUpRight, GitBranch,
} from 'lucide-react'
import { api } from '../api'
import { CodePeek, InsightCallout, type PeekTarget } from './intelligenceUi'
import type {
  IntelligenceOverview, DomainFact, SemanticSymbolHit,
  DependencyNode, ProjectManifest,
} from '../types'

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

type View = 'overview' | 'api' | 'deps' | 'manifests'

const VIEWS: { id: View; label: string; icon: React.ElementType }[] = [
  { id: 'overview',  label: 'Overview',     icon: Sparkles },
  { id: 'api',       label: 'API Surface',  icon: Network  },
  { id: 'deps',      label: 'Dependencies', icon: GitBranch },
  { id: 'manifests', label: 'Manifests',    icon: Package  },
]

// ── Overview: stat cards ────────────────────────────────────────────────────────

function StatCard({
  icon: Icon, label, value, tone, onClick,
}: {
  icon: React.ElementType; label: string; value: number; tone: string; onClick?: () => void
}) {
  return (
    <button
      onClick={onClick}
      disabled={!onClick}
      className={`text-left rounded-xl border border-zinc-800 bg-zinc-900/60 p-4 transition-colors ${
        onClick ? 'hover:border-zinc-700 hover:bg-zinc-800/40 cursor-pointer' : 'cursor-default'
      }`}
    >
      <div className="flex items-center gap-2 mb-2">
        <Icon className={`w-4 h-4 ${tone}`} />
        <span className="text-xs text-zinc-500">{label}</span>
      </div>
      <div className="text-2xl font-semibold text-zinc-100 tabular-nums">{value.toLocaleString()}</div>
    </button>
  )
}

function OverviewView({ data, onJump }: { data: IntelligenceOverview; onJump: (v: View) => void }) {
  const hasInsights = data.orphanSymbols > 0 || data.securitySinks > 0
  return (
    <div className="space-y-4">
      {hasInsights && (
        <div className="flex flex-wrap gap-2">
          <InsightCallout icon={Trash2}      count={data.orphanSymbols} label="orphan symbols (no references)" tone="border-orange-500/30 bg-orange-500/10 text-orange-400" onClick={() => onJump('api')} />
          <InsightCallout icon={ShieldAlert} count={data.securitySinks} label="security-sensitive sinks"          tone="border-red-500/30 bg-red-500/10 text-red-400"        onClick={() => onJump('api')} />
        </div>
      )}
      <div className="grid grid-cols-2 sm:grid-cols-3 lg:grid-cols-4 gap-3">
      <StatCard icon={FileCode2}  label="Files"          value={data.files}           tone="text-zinc-400" />
      <StatCard icon={Boxes}      label="Symbols"        value={data.symbols}         tone="text-indigo-400" />
      <StatCard icon={Route}      label="Endpoints"      value={data.endpoints}       tone="text-emerald-400" onClick={() => onJump('api')} />
      <StatCard icon={Network}    label="DI edges"       value={data.diEdges}         tone="text-sky-400"     onClick={() => onJump('api')} />
      <StatCard icon={Database}   label="EF entities"    value={data.efEntities}      tone="text-amber-400"   onClick={() => onJump('api')} />
      <StatCard icon={Layers}     label="MediatR msgs"   value={data.mediatrMessages} tone="text-fuchsia-400" onClick={() => onJump('api')} />
      <StatCard icon={GitBranch}  label="Type relations" value={data.typeRelations}   tone="text-violet-400"  onClick={() => onJump('api')} />
      <StatCard icon={Settings2}  label="Config keys"    value={data.configKeys}      tone="text-cyan-400"    onClick={() => onJump('api')} />
      <StatCard icon={ShieldAlert} label="Security sinks" value={data.securitySinks}  tone="text-red-400"     onClick={() => onJump('api')} />
      <StatCard icon={Trash2}     label="Orphan symbols" value={data.orphanSymbols}   tone="text-orange-400" />
      <StatCard icon={FlaskConical} label="Test files"   value={data.testFiles}       tone="text-green-400" />
      <StatCard icon={Package}    label="Packages"       value={data.packages}        tone="text-zinc-400"   onClick={() => onJump('manifests')} />
      </div>
    </div>
  )
}

// ── API surface: domain facts grouped ───────────────────────────────────────────

const METHOD_COLOR: Record<string, string> = {
  GET: 'text-emerald-400', POST: 'text-amber-400', PUT: 'text-sky-400',
  PATCH: 'text-violet-400', DELETE: 'text-red-400',
}

type FactGroup = { id: string; label: string; icon: React.ElementType; kinds: string[] }
const FACT_GROUPS: FactGroup[] = [
  { id: 'endpoints', label: 'Endpoints',           icon: Route,       kinds: ['http-endpoint', 'fetch-endpoint'] },
  { id: 'di',        label: 'Dependency Injection', icon: Network,     kinds: ['di-injection'] },
  { id: 'data',      label: 'Data Model',           icon: Database,    kinds: ['ef-entity'] },
  { id: 'messaging', label: 'Messaging',            icon: Layers,      kinds: ['mediatr-message', 'mediatr-handler'] },
  { id: 'types',     label: 'Type Relations',       icon: GitBranch,   kinds: ['type-relation'] },
  { id: 'config',    label: 'Config',               icon: Settings2,   kinds: ['config-key'] },
  { id: 'security',  label: 'Security Sinks',       icon: ShieldAlert, kinds: ['security-sink'] },
]

function FactRow({ f, onPeek }: { f: DomainFact; onPeek: (f: DomainFact) => void }) {
  const isEndpoint = f.kind === 'http-endpoint' || f.kind === 'fetch-endpoint'
  return (
    <button
      onClick={() => onPeek(f)}
      className="w-full text-left flex items-center gap-3 px-3 py-2 rounded-lg hover:bg-zinc-800/50 border border-transparent hover:border-zinc-700 transition-colors"
    >
      {isEndpoint && f.method && (
        <span className={`text-[10px] font-bold font-mono w-12 flex-shrink-0 ${METHOD_COLOR[f.method] ?? 'text-zinc-400'}`}>
          {f.method}
        </span>
      )}
      <span className="flex-1 min-w-0">
        <span className="block font-mono text-sm text-zinc-200 truncate">
          {f.route || f.name || f.typeRef || '—'}
        </span>
        <span className="block text-[11px] text-zinc-500 truncate font-mono">
          {f.kind === 'di-injection' && `${f.ownerType} ← ${f.typeRef}`}
          {f.kind === 'type-relation' && `${f.ownerType} ${f.method} ${f.name}`}
          {f.kind === 'mediatr-handler' && `handles ${f.ownerType}`}
          {f.kind === 'ef-entity' && (f.typeRef ? `table: ${f.typeRef}` : f.relativePath)}
          {f.kind === 'security-sink' && f.typeRef}
          {(f.kind === 'http-endpoint' || f.kind === 'config-key' || f.kind === 'fetch-endpoint' || f.kind === 'mediatr-message') && f.relativePath}
        </span>
      </span>
      {f.name && f.kind === 'security-sink' && (
        <span className="text-[10px] px-1.5 py-0.5 rounded bg-red-500/15 text-red-400 border border-red-500/25 flex-shrink-0">{f.name}</span>
      )}
      <span className="text-[11px] text-zinc-600 tabular-nums flex-shrink-0">:{f.line}</span>
    </button>
  )
}

function ApiSurfaceView({
  projectId, subProjectId, onPeek,
}: {
  projectId: string; subProjectId?: string; onPeek: (f: DomainFact) => void
}) {
  const { data, isLoading, error } = useQuery({
    queryKey: ['domain-facts', projectId, subProjectId],
    queryFn: () => api.intelligence.getDomainFacts(projectId, undefined, subProjectId),
    staleTime: 30_000,
  })

  if (isLoading) return <Centered><Loader2 className="w-5 h-5 animate-spin text-zinc-500" /></Centered>
  if (error)     return <ErrorBox text="Failed to load API surface." />
  const facts = data ?? []
  if (facts.length === 0) return <Empty icon={Network} text="No domain facts indexed yet. Re-index the project to populate routes, DI, and the cache graph." />

  return (
    <div className="space-y-6">
      {FACT_GROUPS.map(group => {
        const items = facts.filter(f => group.kinds.includes(f.kind))
        if (items.length === 0) return null
        const Icon = group.icon
        return (
          <section key={group.id}>
            <h3 className="flex items-center gap-2 text-sm font-medium text-zinc-300 mb-2">
              <Icon className="w-4 h-4 text-zinc-500" />
              {group.label}
              <span className="text-xs text-zinc-600 tabular-nums">{items.length}</span>
            </h3>
            <div className="space-y-0.5">
              {items.slice(0, 200).map((f, i) => <FactRow key={i} f={f} onPeek={onPeek} />)}
            </div>
          </section>
        )
      })}
    </div>
  )
}

// ── Dependencies: hotspots + entrypoints ────────────────────────────────────────

function NodeBar({ node, max, kind, onNavigate }: {
  node: DependencyNode; max: number; kind: 'in' | 'out'; onNavigate?: (id: string) => void
}) {
  const val = kind === 'in' ? node.fanIn : node.fanOut
  const pct = max > 0 ? Math.round((val / max) * 100) : 0
  return (
    <button
      onClick={() => onNavigate?.(node.id)}
      className="w-full text-left group"
    >
      <div className="flex items-center gap-2 mb-0.5">
        <span className="flex-1 min-w-0 font-mono text-xs text-zinc-300 truncate group-hover:text-indigo-300 transition-colors">
          {node.relativePath}
        </span>
        <span className="text-xs text-zinc-500 tabular-nums flex items-center gap-1">
          {kind === 'in' ? <ArrowDownLeft className="w-3 h-3" /> : <ArrowUpRight className="w-3 h-3" />}{val}
        </span>
      </div>
      <div className="h-1.5 rounded-full bg-zinc-800 overflow-hidden">
        <div className={`h-full ${kind === 'in' ? 'bg-indigo-500/70' : 'bg-emerald-500/70'}`} style={{ width: `${pct}%` }} />
      </div>
    </button>
  )
}

function DependenciesView({ projectId, onNavigate }: { projectId: string; onNavigate?: (id: string) => void }) {
  const hot = useQuery({ queryKey: ['hotspots', projectId], queryFn: () => api.intelligence.getHotspots(projectId, 15), staleTime: 30_000 })
  const ep  = useQuery({ queryKey: ['entrypoints', projectId], queryFn: () => api.intelligence.getEntrypoints(projectId), staleTime: 30_000 })

  if (hot.isLoading || ep.isLoading) return <Centered><Loader2 className="w-5 h-5 animate-spin text-zinc-500" /></Centered>
  const hotspots = hot.data ?? []
  const entry = (ep.data ?? []).slice(0, 15)
  const maxIn = Math.max(1, ...hotspots.map(n => n.fanIn))
  const maxOut = Math.max(1, ...entry.map(n => n.fanOut))

  return (
    <div className="grid md:grid-cols-2 gap-6">
      <section>
        <h3 className="flex items-center gap-2 text-sm font-medium text-zinc-300 mb-3">
          <ArrowDownLeft className="w-4 h-4 text-indigo-400" /> Hotspots
          <span className="text-xs text-zinc-600">most depended-on — highest blast radius</span>
        </h3>
        {hotspots.length === 0 ? <Empty icon={GitBranch} text="No dependency data yet." /> : (
          <div className="space-y-2.5">
            {hotspots.map(n => <NodeBar key={n.id} node={n} max={maxIn} kind="in" onNavigate={onNavigate} />)}
          </div>
        )}
      </section>
      <section>
        <h3 className="flex items-center gap-2 text-sm font-medium text-zinc-300 mb-3">
          <Zap className="w-4 h-4 text-emerald-400" /> Entrypoints
          <span className="text-xs text-zinc-600">nothing depends on these</span>
        </h3>
        {entry.length === 0 ? <Empty icon={Zap} text="No entrypoints found." /> : (
          <div className="space-y-2.5">
            {entry.map(n => <NodeBar key={n.id} node={n} max={maxOut} kind="out" onNavigate={onNavigate} />)}
          </div>
        )}
      </section>
    </div>
  )
}

// ── Manifests ─────────────────────────────────────────────────────────────────

function ManifestCard({ m }: { m: ProjectManifest }) {
  const [open, setOpen] = useState(false)
  return (
    <div className="rounded-xl border border-zinc-800 bg-zinc-900/60 overflow-hidden">
      <div className="px-4 py-3 flex items-center gap-2 border-b border-zinc-800">
        <Package className="w-4 h-4 text-zinc-500" />
        <span className="font-mono text-sm text-zinc-200 truncate flex-1 min-w-0">{m.manifestPath.split(/[\\/]/).pop()}</span>
        {m.targetFrameworks.map(t => (
          <span key={t} className="text-[10px] px-1.5 py-0.5 rounded bg-indigo-500/15 text-indigo-300 border border-indigo-500/25">{t}</span>
        ))}
        {m.outputKind && <span className="text-[10px] px-1.5 py-0.5 rounded bg-zinc-800 text-zinc-400 border border-zinc-700">{m.outputKind}</span>}
      </div>
      <div className="px-4 py-3">
        <button onClick={() => setOpen(o => !o)} className="text-xs text-zinc-400 hover:text-zinc-200 transition-colors">
          {m.packages.length} package{m.packages.length !== 1 ? 's' : ''}
          {m.projectReferences.length > 0 && ` · ${m.projectReferences.length} project ref${m.projectReferences.length !== 1 ? 's' : ''}`}
          {open ? ' ▾' : ' ▸'}
        </button>
        {open && (
          <div className="mt-2 flex flex-wrap gap-1.5">
            {m.packages.map(p => (
              <span key={p.name} className={`text-[11px] font-mono px-1.5 py-0.5 rounded border ${
                p.isDev ? 'bg-zinc-800 text-zinc-500 border-zinc-700' : 'bg-zinc-800 text-zinc-300 border-zinc-700'
              }`} title={p.version}>
                {p.name}<span className="text-zinc-600"> {p.version}</span>
              </span>
            ))}
          </div>
        )}
      </div>
    </div>
  )
}

function ManifestsView({ projectId }: { projectId: string }) {
  const { data, isLoading, error } = useQuery({
    queryKey: ['manifests', projectId],
    queryFn: () => api.intelligence.getManifests(projectId),
    staleTime: 60_000,
  })
  if (isLoading) return <Centered><Loader2 className="w-5 h-5 animate-spin text-zinc-500" /></Centered>
  if (error)     return <ErrorBox text="Failed to load manifests." />
  const manifests = data ?? []
  if (manifests.length === 0) return <Empty icon={Package} text="No manifests captured. Re-activate the project to parse .csproj / package.json." />
  return <div className="space-y-3">{manifests.map((m, i) => <ManifestCard key={i} m={m} />)}</div>
}

// ── Shared bits ─────────────────────────────────────────────────────────────────

const Centered = ({ children }: { children: React.ReactNode }) =>
  <div className="flex items-center justify-center py-16">{children}</div>

const ErrorBox = ({ text }: { text: string }) =>
  <div className="rounded-lg border border-red-800/50 bg-red-950/30 px-4 py-3 flex items-center gap-2 text-sm text-red-400">
    <AlertCircle className="w-4 h-4 flex-shrink-0" />{text}
  </div>

const Empty = ({ icon: Icon, text }: { icon: React.ElementType; text: string }) =>
  <div className="py-16 text-center space-y-3">
    <Icon className="w-8 h-8 text-zinc-700 mx-auto" />
    <p className="text-zinc-500 text-sm max-w-md mx-auto">{text}</p>
  </div>

// ── Main hub ────────────────────────────────────────────────────────────────────

export function CodeIntelligence({
  projectId, subProjectId, onNavigateToFile,
}: {
  projectId: string; subProjectId?: string; onNavigateToFile?: (fileId: string) => void
}) {
  const [view, setView] = useState<View>('overview')
  const [search, setSearch] = useState('')
  const [peek, setPeek] = useState<PeekTarget | null>(null)
  const q = useDebounce(search, 300)

  const overview = useQuery({
    queryKey: ['intel-overview', projectId, subProjectId],
    queryFn: () => api.intelligence.getOverview(projectId, subProjectId),
    staleTime: 30_000,
  })

  const semantic = useQuery({
    queryKey: ['semantic', projectId, subProjectId, q],
    queryFn: () => api.intelligence.searchSemantic(projectId, q, 20, subProjectId),
    enabled: q.trim().length > 1,
    staleTime: 30_000,
    placeholderData: keepPreviousData,
  })

  const peekFact = (f: DomainFact) =>
    setPeek({ fileId: f.fileId, title: `${f.relativePath || f.name || ''}`, startLine: Math.max(1, f.line - 1), endLine: f.line + 8 })

  const peekHit = (h: SemanticSymbolHit) =>
    setPeek({ fileId: h.fileId, title: `${h.containingType ? h.containingType + '.' : ''}${h.symbolName}`, symbolName: h.symbolName })

  return (
    <div className="space-y-5">
      {/* Semantic search bar — "find the method that does X", no grep */}
      <div className="relative">
        <Sparkles className="absolute left-3 top-1/2 -translate-y-1/2 w-4 h-4 text-indigo-400 pointer-events-none" />
        <input
          value={search}
          onChange={e => setSearch(e.target.value)}
          placeholder="Semantic symbol search — describe what you're looking for…"
          className="w-full bg-zinc-800 border border-zinc-700 rounded-lg pl-10 pr-9 py-2.5 text-sm text-zinc-100 placeholder-zinc-500 focus:outline-none focus:ring-2 focus:ring-indigo-500 focus:border-transparent"
        />
        {search && (
          <button onClick={() => setSearch('')} className="absolute right-2.5 top-1/2 -translate-y-1/2 p-0.5 text-zinc-500 hover:text-zinc-300 rounded transition-colors">
            <X className="w-4 h-4" />
          </button>
        )}
      </div>

      {/* Semantic results overlay the views when searching */}
      {q.trim().length > 1 ? (
        <div className="space-y-1.5">
          <div className="flex items-center gap-2 text-xs text-zinc-500">
            <Search className="w-3.5 h-3.5" />
            {semantic.isFetching ? 'Searching…' : `${semantic.data?.length ?? 0} semantic matches`}
          </div>
          {(semantic.data ?? []).map(h => (
            <button
              key={h.id}
              onClick={() => peekHit(h)}
              className="w-full text-left flex items-center gap-3 px-3 py-2 rounded-lg border border-zinc-800 hover:border-zinc-700 hover:bg-zinc-800/40 transition-colors"
            >
              <Boxes className="w-3.5 h-3.5 text-indigo-400 flex-shrink-0" />
              <span className="flex-1 min-w-0">
                <span className="block font-mono text-sm text-zinc-200 truncate">
                  {h.containingType ? <span className="text-zinc-500">{h.containingType}.</span> : null}{h.symbolName}
                </span>
                <span className="block text-[11px] text-zinc-500 font-mono truncate">{h.relativePath}:{h.line}</span>
              </span>
              <span className="text-xs text-zinc-600">{h.kind}</span>
              <span className="text-[10px] px-1.5 py-0.5 rounded bg-indigo-500/15 text-indigo-300 border border-indigo-500/25 tabular-nums flex-shrink-0">
                {(h.score * 100).toFixed(0)}%
              </span>
            </button>
          ))}
          {semantic.data?.length === 0 && !semantic.isFetching && (
            <Empty icon={Search} text="No semantic matches. Per-symbol embeddings populate after ingestion when the embedding model is available." />
          )}
        </div>
      ) : (
        <>
          {/* Sub-view nav */}
          <div className="flex gap-1 border-b border-zinc-800">
            {VIEWS.map(v => {
              const Icon = v.icon
              return (
                <button
                  key={v.id}
                  onClick={() => setView(v.id)}
                  className={`flex items-center gap-2 px-3.5 py-2 text-sm font-medium border-b-2 -mb-px transition-colors ${
                    view === v.id ? 'border-indigo-500 text-indigo-400' : 'border-transparent text-zinc-500 hover:text-zinc-300'
                  }`}
                >
                  <Icon className="w-4 h-4" />
                  {v.label}
                </button>
              )
            })}
          </div>

          {view === 'overview' && (
            overview.isLoading ? <Centered><Loader2 className="w-5 h-5 animate-spin text-zinc-500" /></Centered>
            : overview.error ? <ErrorBox text="Failed to load overview." />
            : overview.data ? <OverviewView data={overview.data} onJump={setView} />
            : null
          )}
          {view === 'api'       && <ApiSurfaceView projectId={projectId} subProjectId={subProjectId} onPeek={peekFact} />}
          {view === 'deps'      && <DependenciesView projectId={projectId} onNavigate={onNavigateToFile} />}
          {view === 'manifests' && <ManifestsView projectId={projectId} />}
        </>
      )}

      {peek && <CodePeek projectId={projectId} target={peek} onClose={() => setPeek(null)} />}
    </div>
  )
}
