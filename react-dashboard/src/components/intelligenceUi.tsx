import { useQuery } from '@tanstack/react-query'
import {
  X, Loader2, AlertCircle, FileCode2, ArrowUpRight, AlertTriangle,
  FlaskConical, Lock, Code2, ShieldX,
} from 'lucide-react'
import { api } from '../api'
import type { SymbolRecord, SymbolReference, FileContent } from '../types'

// ── Code peek modal: reads an exact line range on demand (no grep, always current) ──────────────

export type PeekTarget = {
  fileId: string
  title: string
  symbolName?: string
  startLine?: number
  endLine?: number
}

export function CodePeek({
  projectId, target, onClose,
}: {
  projectId: string
  target: PeekTarget
  onClose: () => void
}) {
  const { data, isLoading, error } = useQuery({
    queryKey: ['peek', projectId, target.fileId, target.symbolName, target.startLine, target.endLine],
    queryFn: (): Promise<FileContent> =>
      target.symbolName
        ? api.intelligence.getSymbolContent(projectId, target.fileId, target.symbolName)
        : api.intelligence.getContent(projectId, target.fileId, target.startLine, target.endLine),
    staleTime: 60_000,
  })

  const lines = data ? data.content.split('\n') : []

  return (
    <div className="fixed inset-0 z-50 flex items-center justify-center bg-black/60 backdrop-blur-sm p-4" onClick={onClose}>
      <div
        className="w-full max-w-3xl max-h-[80vh] flex flex-col bg-zinc-900 border border-zinc-700 rounded-xl shadow-2xl overflow-hidden"
        onClick={e => e.stopPropagation()}
      >
        <div className="flex items-center gap-2 px-4 py-3 border-b border-zinc-800 bg-zinc-900/80">
          <FileCode2 className="w-4 h-4 text-indigo-400 flex-shrink-0" />
          <span className="flex-1 min-w-0 font-mono text-sm text-zinc-200 truncate">{target.title}</span>
          {data && <span className="text-[11px] text-zinc-500 tabular-nums">L{data.startLine}–{data.endLine}</span>}
          {data?.stale && (
            <span className="text-[10px] px-1.5 py-0.5 rounded bg-amber-500/15 text-amber-400 border border-amber-500/25">stale</span>
          )}
          <button onClick={onClose} className="p-1 text-zinc-500 hover:text-zinc-200 hover:bg-zinc-800 rounded transition-colors">
            <X className="w-4 h-4" />
          </button>
        </div>
        <div className="overflow-auto">
          {isLoading ? (
            <div className="flex items-center justify-center py-16"><Loader2 className="w-5 h-5 animate-spin text-zinc-500" /></div>
          ) : error ? (
            <div className="px-4 py-6 text-sm text-red-400 flex items-center gap-2"><AlertCircle className="w-4 h-4" /> Could not read file content.</div>
          ) : (
            <pre className="text-[12.5px] leading-relaxed font-mono text-zinc-300 p-0 m-0">
              {lines.map((ln, i) => (
                <div key={i} className="flex hover:bg-zinc-800/40">
                  <span className="select-none text-right text-zinc-600 w-12 flex-shrink-0 pr-3 tabular-nums">{(data?.startLine ?? 1) + i}</span>
                  <code className="flex-1 whitespace-pre-wrap break-words pr-4">{ln || ' '}</code>
                </div>
              ))}
            </pre>
          )}
        </div>
      </div>
    </div>
  )
}

// ── Badges ──────────────────────────────────────────────────────────────────────

export const ACCESS_COLOR: Record<string, string> = {
  public:    'bg-green-500/10 text-green-400',
  exported:  'bg-green-500/10 text-green-400',
  internal:  'bg-amber-500/10 text-amber-400',
  protected: 'bg-sky-500/10 text-sky-400',
  private:   'bg-zinc-700/60 text-zinc-500',
}

export function AccessBadge({ value }: { value: string }) {
  return <span className={`text-[10px] px-1.5 py-0.5 rounded font-medium ${ACCESS_COLOR[value] ?? ACCESS_COLOR.private}`}>{value}</span>
}

export function Chip({ children, tone = 'zinc', title }: { children: React.ReactNode; tone?: string; title?: string }) {
  const tones: Record<string, string> = {
    zinc:    'bg-zinc-800 text-zinc-400 border-zinc-700',
    indigo:  'bg-indigo-500/15 text-indigo-300 border-indigo-500/25',
    amber:   'bg-amber-500/15 text-amber-400 border-amber-500/25',
    red:     'bg-red-500/15 text-red-400 border-red-500/25',
    green:   'bg-green-500/15 text-green-400 border-green-500/25',
    violet:  'bg-violet-500/15 text-violet-300 border-violet-500/25',
    sky:     'bg-sky-500/15 text-sky-300 border-sky-500/25',
    orange:  'bg-orange-500/15 text-orange-400 border-orange-500/25',
  }
  return (
    <span title={title} className={`inline-flex items-center gap-1 text-[10px] font-medium px-1.5 py-0.5 rounded border ${tones[tone] ?? tones.zinc}`}>
      {children}
    </span>
  )
}

export function ModifierBadges({ s }: { s: SymbolRecord }) {
  const mods: string[] = []
  if (s.isStatic)   mods.push('static')
  if (s.isAbstract) mods.push('abstract')
  if (s.isVirtual)  mods.push('virtual')
  if (s.isOverride) mods.push('override')
  if (s.isSealed)   mods.push('sealed')
  if (s.isAsync)    mods.push('async')
  return <>{mods.map(m => <Chip key={m} tone="zinc">{m}</Chip>)}</>
}

export function ContractBadges({ s }: { s: SymbolRecord }) {
  return (
    <>
      {s.implementsIDisposable      && <Chip tone="sky" title="implements IDisposable">IDisposable</Chip>}
      {s.implementsIAsyncDisposable && <Chip tone="sky" title="implements IAsyncDisposable">IAsyncDisposable</Chip>}
      {s.isBackgroundService        && <Chip tone="violet" title="IHostedService / BackgroundService"><FlaskConical className="w-2.5 h-2.5" />worker</Chip>}
      {s.hasStaticMutableState      && <Chip tone="amber" title="has static mutable state">static state</Chip>}
      {s.usesLock                   && <Chip tone="amber" title="uses lock {}"><Lock className="w-2.5 h-2.5" />lock</Chip>}
      {s.blocksOnAsync              && <Chip tone="red" title=".Result / .Wait() — blocks on async">blocks-on-async</Chip>}
      {s.isAsyncEnumerable          && <Chip tone="indigo">IAsyncEnumerable</Chip>}
    </>
  )
}

export function StatusBadges({ s, reference: r }: { s?: SymbolRecord; reference?: SymbolReference }) {
  return (
    <>
      {s?.isDeprecated && <Chip tone="orange" title={s.deprecationMessage ?? 'deprecated'}><AlertTriangle className="w-2.5 h-2.5" />deprecated</Chip>}
      {(r?.testedByFileIds?.length ?? 0) > 0 && <Chip tone="green" title={`covered by ${r!.testedByFileIds!.length} test file(s)`}><FlaskConical className="w-2.5 h-2.5" />tested</Chip>}
    </>
  )
}

// ── Signature ───────────────────────────────────────────────────────────────────

const CALLABLE = new Set(['method', 'function', 'constructor'])

export function SymbolSignature({ s }: { s: SymbolRecord }) {
  const tp = s.typeParameters?.length ? `<${s.typeParameters.map(t => t.name).join(', ')}>` : ''
  const ret = s.returnTypeUnwrapped || s.type
  const isCallable = (s.parameters?.length ?? 0) > 0 || CALLABLE.has(s.kind.toLowerCase())

  return (
    <code className="font-mono text-sm break-words leading-relaxed">
      <span className="text-zinc-100">{s.name}</span><span className="text-zinc-500">{tp}</span>
      {isCallable ? (
        <>
          <span className="text-zinc-600">(</span>
          {(s.parameters ?? []).map((p, i) => (
            <span key={i}>
              {i > 0 && <span className="text-zinc-600">, </span>}
              {p.isParams && <span className="text-zinc-500">params </span>}
              {p.refKind && p.refKind !== 'none' && <span className="text-zinc-500">{p.refKind} </span>}
              <span className="text-zinc-300">{p.name}</span>
              <span className="text-zinc-600">: </span>
              <span className="text-indigo-300">{p.type}</span>
              {p.isOptional && <span className="text-zinc-600">?</span>}
            </span>
          ))}
          <span className="text-zinc-600">)</span>
          {ret && <><span className="text-zinc-600"> : </span><span className="text-indigo-300">{ret}</span></>}
        </>
      ) : (
        ret && <><span className="text-zinc-600"> : </span><span className="text-indigo-300">{ret}</span></>
      )}
    </code>
  )
}

// ── Caller rows (from the symbol→symbol call graph) ─────────────────────────────

const ROLE_COLOR: Record<string, string> = {
  call: 'text-indigo-400', new: 'text-emerald-400', write: 'text-amber-400',
  read: 'text-zinc-500', typeref: 'text-sky-400', implements: 'text-violet-400',
  override: 'text-violet-400', import: 'text-zinc-500', ref: 'text-zinc-500',
}

// ── The rich symbol detail panel — reused by the Symbols and Files tabs ─────────

export function RichSymbolDetail({
  record, reference, fileId, onPeek, onNavigateToFile,
}: {
  record?: SymbolRecord
  reference?: SymbolReference
  fileId: string
  onPeek: (t: PeekTarget) => void
  onNavigateToFile?: (fileId: string) => void
}) {
  const name = record?.name ?? reference?.name ?? ''
  const usedBy = reference?.usedBy ?? []

  return (
    <div className="space-y-3 text-sm">
      {/* Signature */}
      {record && (
        <div className="flex items-start gap-3">
          <div className="flex-1 min-w-0 bg-zinc-950/40 rounded-lg px-3 py-2 border border-zinc-800">
            <SymbolSignature s={record} />
          </div>
          <button
            onClick={() => onPeek({ fileId, title: `${record.containingTypeFullName ? record.containingTypeFullName + '.' : ''}${name}`, symbolName: name })}
            className="flex-shrink-0 flex items-center gap-1.5 px-2.5 py-1.5 text-xs text-indigo-400 hover:text-indigo-300 bg-indigo-500/10 hover:bg-indigo-500/20 border border-indigo-500/25 rounded-lg transition-colors"
          >
            <Code2 className="w-3.5 h-3.5" /> View source
          </button>
        </div>
      )}

      {/* Badges */}
      <div className="flex flex-wrap items-center gap-1.5">
        {record && <span className="text-xs text-zinc-500">{record.kind}</span>}
        {record && <AccessBadge value={record.accessibility} />}
        {record && <ModifierBadges s={record} />}
        {record && <ContractBadges s={record} />}
        <StatusBadges s={record} reference={reference} />
      </div>

      {/* Deprecation */}
      {record?.isDeprecated && (
        <div className="flex items-start gap-2 rounded-lg border border-orange-800/50 bg-orange-950/20 px-3 py-2">
          <AlertTriangle className="w-4 h-4 text-orange-400 flex-shrink-0 mt-0.5" />
          <p className="text-xs text-orange-300">{record.deprecationMessage || 'This API is marked obsolete.'}</p>
        </div>
      )}

      {/* Doc */}
      {record?.docSummary && (
        <p className="text-sm text-zinc-300 leading-relaxed border-l-2 border-zinc-700 pl-3">{record.docSummary}</p>
      )}
      {record?.docRemarks && (
        <p className="text-xs text-zinc-500 leading-relaxed pl-3">{record.docRemarks}</p>
      )}

      {/* Parameters */}
      {record && record.parameters?.length > 0 && (
        <div>
          <p className="text-[10px] uppercase tracking-wide text-zinc-500 mb-1">Parameters</p>
          <div className="space-y-1">
            {record.parameters.map((p, i) => (
              <div key={i} className="flex items-baseline gap-2 text-xs">
                <span className="font-mono text-zinc-300">{p.name}</span>
                <span className="font-mono text-indigo-300/80">{p.type}</span>
                {p.defaultValue != null && <span className="font-mono text-zinc-600">= {p.defaultValue}</span>}
                {record.paramDocs?.[p.name] && <span className="text-zinc-500 truncate">— {record.paramDocs[p.name]}</span>}
              </div>
            ))}
          </div>
        </div>
      )}

      {record?.returnsDoc && (
        <div className="text-xs"><span className="text-zinc-500">Returns: </span><span className="text-zinc-300">{record.returnsDoc}</span></div>
      )}

      {/* Thrown / documented exceptions */}
      {((record?.thrownExceptions?.length ?? 0) > 0 || (record?.documentedExceptions?.length ?? 0) > 0) && (
        <div className="flex flex-wrap items-center gap-1.5">
          <span className="text-[10px] uppercase tracking-wide text-zinc-500 flex items-center gap-1"><ShieldX className="w-3 h-3" />Throws</span>
          {[...new Set([...(record?.thrownExceptions ?? []), ...(record?.documentedExceptions ?? [])])].map(ex => (
            <Chip key={ex} tone="red">{ex}</Chip>
          ))}
        </div>
      )}

      {/* Type structure */}
      {(record?.baseChain?.length || record?.interfaces?.length) ? (
        <div className="flex flex-wrap items-center gap-1.5">
          {record.baseChain?.map(b => <Chip key={b} tone="violet" title="base type">: {b}</Chip>)}
          {record.interfaces?.slice(0, 12).map(i => <Chip key={i} tone="sky" title="interface">{i}</Chip>)}
        </div>
      ) : null}

      {/* Callers — symbol→symbol call graph */}
      <div>
        <p className="text-[10px] uppercase tracking-wide text-zinc-500 mb-1.5">
          {usedBy.length === 0 ? 'No recorded references' : `Referenced from ${usedBy.length} site${usedBy.length !== 1 ? 's' : ''}`}
        </p>
        {usedBy.length > 0 && (
          <div className="space-y-0.5 max-h-64 overflow-y-auto">
            {usedBy.slice(0, 60).map((u, i) => (
              <div key={i} className="flex items-center gap-2 py-1 border-b border-zinc-800/50 last:border-0">
                <span className={`text-[10px] font-mono w-14 flex-shrink-0 ${ROLE_COLOR[u.role ?? 'ref'] ?? 'text-zinc-500'}`}>{u.role ?? 'ref'}</span>
                <span className="flex-1 min-w-0">
                  <span className="block font-mono text-xs text-zinc-300 truncate">
                    {u.enclosingName ? <span className="text-zinc-100">{u.enclosingName}</span> : null}
                    {u.enclosingName ? <span className="text-zinc-600"> · </span> : null}
                    <span className="text-zinc-500">{u.relativePath}:{u.line}</span>
                  </span>
                </span>
                <button
                  onClick={() => onPeek({ fileId: u.fileId, title: `${u.relativePath}:${u.line}`, startLine: Math.max(1, u.line - 3), endLine: u.line + 5 })}
                  className="flex-shrink-0 text-[10px] text-zinc-500 hover:text-indigo-300 transition-colors"
                >peek</button>
                {onNavigateToFile && (
                  <button onClick={() => onNavigateToFile(u.fileId)} className="flex-shrink-0 text-[10px] text-indigo-400 hover:text-indigo-300 transition-colors flex items-center gap-0.5">
                    <ArrowUpRight className="w-3 h-3" />
                  </button>
                )}
              </div>
            ))}
          </div>
        )}
      </div>
    </div>
  )
}

// Small helper used by overview insights
export function InsightCallout({
  icon: Icon, count, label, tone, onClick,
}: {
  icon: React.ElementType; count: number; label: string; tone: string; onClick?: () => void
}) {
  if (count <= 0) return null
  return (
    <button
      onClick={onClick}
      disabled={!onClick}
      className={`flex items-center gap-2.5 rounded-lg border px-3 py-2 transition-colors ${tone} ${onClick ? 'hover:brightness-125 cursor-pointer' : 'cursor-default'}`}
    >
      <Icon className="w-4 h-4 flex-shrink-0" />
      <span className="text-sm font-semibold tabular-nums">{count.toLocaleString()}</span>
      <span className="text-xs opacity-80">{label}</span>
    </button>
  )
}
