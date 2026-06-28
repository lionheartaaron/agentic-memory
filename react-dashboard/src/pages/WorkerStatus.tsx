import { useState } from 'react'
import { useQuery, useMutation, useQueryClient } from '@tanstack/react-query'
import { Link } from 'react-router-dom'
import {
  Activity, CheckCircle2, XCircle, Loader2,
  ChevronRight, RefreshCw, Layers, List, FileText,
  AlertTriangle, Sparkles, GitBranch,
} from 'lucide-react'
import { api } from '../api'
import type { SubProjectStatus, CodeIndexFile } from '../types'

type Tab = 'queue' | 'summary' | 'references' | 'indexed' | 'stale' | 'errors'

const LANG_SHORT: Record<string, string> = {
  csharp: 'cs', typescript: 'ts', javascript: 'js',
  python: 'py', go: 'go', rust: 'rs', unknown: '?',
}

const LANG_BADGE: Record<string, string> = {
  csharp:     'bg-violet-500/15 text-violet-300 border-violet-500/25',
  typescript: 'bg-blue-500/15 text-blue-300 border-blue-500/25',
  javascript: 'bg-yellow-500/15 text-yellow-300 border-yellow-500/25',
  python:     'bg-green-500/15 text-green-300 border-green-500/25',
  go:         'bg-cyan-500/15 text-cyan-300 border-cyan-500/25',
  rust:       'bg-orange-500/15 text-orange-300 border-orange-500/25',
}

function LangBadge({ lang }: { lang: string }) {
  const key = lang.toLowerCase()
  const cls = LANG_BADGE[key] ?? 'bg-zinc-700/40 text-zinc-400 border-zinc-600/25'
  return (
    <span className={`inline-flex items-center px-1.5 py-0.5 rounded text-[10px] font-bold border flex-shrink-0 ${cls}`}>
      {LANG_SHORT[key] ?? lang.slice(0, 3)}
    </span>
  )
}

function ProgressBar({ value, max, color = 'bg-indigo-500' }: { value: number; max: number; color?: string }) {
  const pct = max > 0 ? Math.min(100, Math.round((value / max) * 100)) : 0
  return (
    <div className="flex items-center gap-3">
      <div className="flex-1 h-1.5 bg-zinc-800 rounded-full overflow-hidden">
        <div
          className={`h-full ${color} rounded-full transition-all duration-500`}
          style={{ width: `${pct}%` }}
        />
      </div>
      <span className="text-xs text-zinc-500 tabular-nums w-24 text-right flex-shrink-0">
        {value} / {max} files
      </span>
    </div>
  )
}

function timeAgo(iso: string) {
  const diff = Date.now() - new Date(iso).getTime()
  if (diff < 5_000) return 'just now'
  if (diff < 60_000) return `${Math.floor(diff / 1_000)}s ago`
  if (diff < 3_600_000) return `${Math.floor(diff / 60_000)}m ago`
  return `${Math.floor(diff / 3_600_000)}h ago`
}

function EmptyState({ icon: Icon, message }: { icon: React.ElementType; message: string }) {
  return (
    <div className="flex flex-col items-center justify-center py-12 text-zinc-600">
      <Icon className="w-8 h-8 mb-3 opacity-40" />
      <p className="text-sm">{message}</p>
    </div>
  )
}

// ── Activity banner (shown above a queue list when something is actively running) ──

function ActiveJobBanner({ file, label, icon: Icon, color }: {
  file: string; label: string; icon: React.ElementType; color: string
}) {
  return (
    <div className={`flex items-center gap-3 px-4 py-2.5 border-b border-zinc-700/60 ${color}`}>
      <Icon className="w-3.5 h-3.5 animate-pulse flex-shrink-0" />
      <span className="text-xs text-zinc-400 flex-shrink-0">{label}</span>
      <span className="flex-1 min-w-0 font-mono text-xs text-zinc-200 truncate">{file}</span>
    </div>
  )
}

function SubProjectRow({ sp, workspaceId }: { sp: SubProjectStatus; workspaceId: string }) {
  const queryClient = useQueryClient()
  const reindexMutation = useMutation({
    mutationFn: () => api.workspaces.reindexSubProject(workspaceId, sp.subProjectId),
    onSuccess: () => queryClient.invalidateQueries({ queryKey: ['worker-status'] }),
  })

  return (
    <div className="flex items-center gap-3 px-4 py-2.5">
      <LangBadge lang={sp.language} />
      <span className="flex-1 min-w-0 text-sm text-zinc-300 truncate" title={sp.name}>
        {sp.name}
      </span>
      <div className="flex items-center gap-3 flex-shrink-0 text-xs tabular-nums">
        <span className="text-zinc-400 w-20 text-right">{sp.indexedFiles} indexed</span>
        {sp.staleFiles > 0 && (
          <span className="text-amber-400 w-16 text-right">{sp.staleFiles} stale</span>
        )}
        {sp.errorFiles > 0 && (
          <span className="text-red-400 w-14 text-right">{sp.errorFiles} err</span>
        )}
        {sp.staleFiles === 0 && sp.errorFiles === 0 && (
          <span className="text-zinc-600 w-[116px] text-right">up to date</span>
        )}
      </div>
      <button
        onClick={() => reindexMutation.mutate()}
        disabled={reindexMutation.isPending}
        className="flex items-center gap-1 px-2 py-1 text-xs text-zinc-500 hover:text-zinc-200 hover:bg-zinc-800 rounded transition-colors disabled:opacity-40 flex-shrink-0"
      >
        {reindexMutation.isPending
          ? <Loader2 className="w-3 h-3 animate-spin" />
          : <RefreshCw className="w-3 h-3" />}
        Reindex
      </button>
    </div>
  )
}

// ── Tab panels ────────────────────────────────────────────────────────────────

function QueuePanel({
  items, currentFile,
}: { items: { relativePath: string; filePath: string }[]; currentFile: string | null }) {
  const hasActivity = !!currentFile
  if (!hasActivity && items.length === 0)
    return <EmptyState icon={List} message="Ingestion queue is empty" />

  return (
    <div>
      {currentFile && (
        <ActiveJobBanner
          file={currentFile}
          label="Indexing"
          icon={Loader2}
          color="bg-blue-950/30 text-blue-400"
        />
      )}
      {items.length === 0 && currentFile ? (
        <div className="px-4 py-3 text-xs text-zinc-600 italic">No files waiting — processing last item</div>
      ) : (
        <div className="divide-y divide-zinc-800/60">
          {items.map((item, i) => (
            <div key={i} className="flex items-center gap-3 px-4 py-2.5">
              <div className="w-3.5 h-3.5 rounded-full border border-zinc-700 flex-shrink-0" />
              <span className="flex-1 min-w-0 font-mono text-xs text-zinc-400 truncate" title={item.filePath}>
                {item.relativePath}
              </span>
            </div>
          ))}
        </div>
      )}
    </div>
  )
}

function SummaryPanel({
  items, currentSummaryFile,
}: { items: { relativePath: string; filePath: string }[]; currentSummaryFile: string | null }) {
  const hasActivity = !!currentSummaryFile
  if (!hasActivity && items.length === 0)
    return <EmptyState icon={Sparkles} message="Summary queue is empty" />

  return (
    <div>
      {currentSummaryFile && (
        <ActiveJobBanner
          file={currentSummaryFile}
          label="Summarizing"
          icon={Sparkles}
          color="bg-indigo-950/30 text-indigo-400"
        />
      )}
      {items.length === 0 && currentSummaryFile ? (
        <div className="px-4 py-3 text-xs text-zinc-600 italic">No files waiting — processing last item</div>
      ) : (
        <div className="divide-y divide-zinc-800/60">
          {items.map((item, i) => (
            <div key={i} className="flex items-center gap-3 px-4 py-2.5">
              <div className="w-3.5 h-3.5 rounded-full border border-indigo-900/60 flex-shrink-0" />
              <span className="flex-1 min-w-0 font-mono text-xs text-zinc-400 truncate" title={item.filePath}>
                {item.relativePath}
              </span>
            </div>
          ))}
        </div>
      )}
    </div>
  )
}

function ReferencePanel({
  currentReferenceFile, depth, totalSymbolReferences,
}: {
  currentReferenceFile: string | null
  depth: number
  totalSymbolReferences: number
}) {
  if (!currentReferenceFile && depth === 0)
    return (
      <div>
        <EmptyState icon={GitBranch} message="Reference analysis queue is empty" />
        {totalSymbolReferences > 0 && (
          <p className="text-center text-xs text-zinc-600 pb-4">
            {totalSymbolReferences.toLocaleString()} symbol references tracked
          </p>
        )}
      </div>
    )
  return (
    <div>
      {currentReferenceFile && (
        <ActiveJobBanner
          file={currentReferenceFile}
          label="Analyzing refs"
          icon={GitBranch}
          color="bg-violet-950/30 text-violet-400"
        />
      )}
      {depth > 0 && (
        <div className="px-4 py-3 text-xs text-zinc-500">
          {depth} file{depth !== 1 ? 's' : ''} pending reference analysis
        </div>
      )}
      {totalSymbolReferences > 0 && (
        <div className="px-4 py-3 border-t border-zinc-800 text-xs text-zinc-600 text-right">
          {totalSymbolReferences.toLocaleString()} symbol references tracked
        </div>
      )}
    </div>
  )
}

function IndexedPanel({ jobs }: { jobs: { relativePath: string; language: string; symbolCount: number; durationMs: number; indexedAt: string; wasNew: boolean }[] }) {
  if (jobs.length === 0) return <EmptyState icon={CheckCircle2} message="No recently indexed files" />
  return (
    <div className="divide-y divide-zinc-800/60">
      {jobs.map((j, i) => (
        <div key={i} className="flex items-center gap-3 px-4 py-2.5">
          <CheckCircle2 className="w-3.5 h-3.5 text-green-400 flex-shrink-0" />
          <span className="flex-1 min-w-0 font-mono text-xs text-zinc-300 truncate">{j.relativePath}</span>
          <LangBadge lang={j.language} />
          {j.wasNew && (
            <span className="text-[10px] bg-indigo-500/15 text-indigo-300 border border-indigo-500/25 px-1.5 py-0.5 rounded flex-shrink-0">new</span>
          )}
          <span className="text-xs text-zinc-500 flex-shrink-0 w-14 text-right tabular-nums">{j.symbolCount} sym</span>
          <span className="text-xs text-zinc-600 flex-shrink-0 w-16 text-right tabular-nums">{j.durationMs}ms</span>
          <span className="text-xs text-zinc-600 flex-shrink-0 w-16 text-right">{timeAgo(j.indexedAt)}</span>
        </div>
      ))}
    </div>
  )
}

function StalePanel({ files, workspaceId }: { files: CodeIndexFile[]; workspaceId: string }) {
  const queryClient = useQueryClient()
  const reindexMutation = useMutation({
    mutationFn: () => api.workspaces.reindex(workspaceId),
    onSuccess: () => {
      queryClient.invalidateQueries({ queryKey: ['worker-status'] })
      queryClient.invalidateQueries({ queryKey: ['stale-files', workspaceId] })
    },
  })

  if (files.length === 0) return <EmptyState icon={AlertTriangle} message="No stale files — index is current" />
  return (
    <div>
      <div className="divide-y divide-zinc-800/60">
        {files.map((f) => (
          <div key={f.id} className="flex items-center gap-3 px-4 py-2.5">
            <AlertTriangle className="w-3.5 h-3.5 text-amber-400 flex-shrink-0" />
            <span className="flex-1 min-w-0 font-mono text-xs text-zinc-300 truncate" title={f.filePath}>
              {f.relativePath}
            </span>
            <LangBadge lang={f.language} />
            <span className="text-xs text-zinc-600 flex-shrink-0 w-28 text-right">
              mod {timeAgo(String(f.fileModifiedAt))}
            </span>
          </div>
        ))}
      </div>
      <div className="px-4 py-3 border-t border-zinc-800 flex justify-end">
        <button
          onClick={() => reindexMutation.mutate()}
          disabled={reindexMutation.isPending}
          className="flex items-center gap-1.5 px-3 py-1.5 bg-amber-600 hover:bg-amber-500 disabled:opacity-40 text-white text-xs font-medium rounded-lg transition-colors"
        >
          {reindexMutation.isPending ? <Loader2 className="w-3 h-3 animate-spin" /> : <RefreshCw className="w-3 h-3" />}
          Reindex stale
        </button>
      </div>
    </div>
  )
}

function ErrorsPanel({ files }: { files: CodeIndexFile[] }) {
  if (files.length === 0) return <EmptyState icon={XCircle} message="No errors" />
  return (
    <div className="divide-y divide-zinc-800/60">
      {files.map((f) => (
        <div key={f.id} className="flex items-start gap-3 px-4 py-2.5">
          <XCircle className="w-3.5 h-3.5 text-red-400 flex-shrink-0 mt-0.5" />
          <div className="flex-1 min-w-0">
            <p className="font-mono text-xs text-zinc-300 truncate" title={f.filePath}>{f.relativePath}</p>
            <p className="text-xs text-red-400 mt-0.5 truncate">{f.ingestionError}</p>
          </div>
          <LangBadge lang={f.language} />
        </div>
      ))}
    </div>
  )
}

// ── Main page ─────────────────────────────────────────────────────────────────

export default function WorkerStatus() {
  const queryClient = useQueryClient()
  const [activeTab, setActiveTab] = useState<Tab>('queue')

  const { data: status, isLoading } = useQuery({
    queryKey: ['worker-status'],
    queryFn: api.codeIndex.workerStatus,
    refetchInterval: (q) => q.state.data?.isProcessing ? 2_000 : 10_000,
    retry: false,
  })

  const { data: projects } = useQuery({
    queryKey: ['projects'],
    queryFn: api.projects.list,
  })

  const staleQuery = useQuery({
    queryKey: ['stale-files', status?.activeProjectId],
    queryFn: () => api.workspaces.staleFiles(status!.activeProjectId!),
    enabled: activeTab === 'stale' && !!status?.activeProjectId,
    refetchInterval: activeTab === 'stale' ? 10_000 : false,
  })

  const errorQuery = useQuery({
    queryKey: ['error-files', status?.activeProjectId],
    queryFn: () => api.workspaces.errorFiles(status!.activeProjectId!),
    enabled: activeTab === 'errors' && !!status?.activeProjectId,
    refetchInterval: activeTab === 'errors' ? 10_000 : false,
  })

  const deactivateMutation = useMutation({
    mutationFn: api.codeIndex.deactivate,
    onSuccess: () => queryClient.invalidateQueries({ queryKey: ['worker-status'] }),
  })

  const activateMutation = useMutation({
    mutationFn: (id: string) => api.codeIndex.activate(id),
    onSuccess: () => queryClient.invalidateQueries({ queryKey: ['worker-status'] }),
  })

  if (isLoading || !status) {
    return (
      <div className="flex items-center justify-center h-64">
        <Loader2 className="w-5 h-5 animate-spin text-zinc-500" />
      </div>
    )
  }

  const s = status
  const doneCount = Math.max(0, s.indexedFiles - s.staleFiles)
  const subProjectStatuses = s.subProjectStatuses ?? []
  const queuedIngestions = s.queuedIngestions ?? []
  const queuedSummaries = s.queuedSummaries ?? []

  const isIndexing    = s.queueDepth          > 0 || !!s.currentFile
  const isSummarizing = s.summaryQueueDepth   > 0 || !!s.currentSummaryFile
  const isReferencing = (s.referenceQueueDepth ?? 0) > 0 || !!s.currentReferenceFile

  const tabs: { id: Tab; label: string; count: number; icon: React.ElementType; activeColor: string }[] = [
    { id: 'queue',      label: 'Queue',      count: s.queueDepth,               icon: List,          activeColor: 'text-blue-400 border-blue-400' },
    { id: 'summary',    label: 'Summary',    count: s.summaryQueueDepth,        icon: Sparkles,      activeColor: 'text-indigo-400 border-indigo-400' },
    { id: 'references', label: 'Ref queue',  count: s.referenceQueueDepth ?? 0, icon: GitBranch,     activeColor: 'text-violet-400 border-violet-400' },
    { id: 'indexed',    label: 'Indexed',    count: s.indexedFiles,             icon: CheckCircle2,  activeColor: 'text-green-400 border-green-400' },
    { id: 'stale',      label: 'Stale',      count: s.staleFiles,               icon: AlertTriangle, activeColor: 'text-amber-400 border-amber-400' },
    { id: 'errors',     label: 'Errors',     count: s.errorFiles,               icon: XCircle,       activeColor: 'text-red-400 border-red-400' },
  ]

  return (
    <div className="p-6 space-y-5">
      {/* Header */}
      <div className="flex items-center justify-between">
        <div className="flex items-center gap-3">
          <div className="w-10 h-10 rounded-xl bg-indigo-500/10 border border-indigo-500/20 flex items-center justify-center flex-shrink-0">
            <Activity className="w-5 h-5 text-indigo-400" />
          </div>
          <div>
            <h1 className="text-xl font-semibold text-zinc-100">Code Index Worker</h1>
            <p className="text-xs text-zinc-500 mt-0.5">Background ingestion &amp; summarization</p>
          </div>
        </div>

        {/* Status badges — one per active worker */}
        <div className="flex items-center gap-2">
          {isIndexing && (
            <div className="flex items-center gap-2 px-3 py-1.5 bg-blue-500/10 border border-blue-500/20 rounded-lg">
              <Loader2 className="w-3 h-3 text-blue-400 animate-spin" />
              <span className="text-xs text-blue-400 font-medium">Indexing</span>
            </div>
          )}
          {isSummarizing && (
            <div className="flex items-center gap-2 px-3 py-1.5 bg-indigo-500/10 border border-indigo-500/20 rounded-lg">
              <Sparkles className="w-3 h-3 text-indigo-400 animate-pulse" />
              <span className="text-xs text-indigo-400 font-medium">Summarizing</span>
            </div>
          )}
          {isReferencing && (
            <div className="flex items-center gap-2 px-3 py-1.5 bg-violet-500/10 border border-violet-500/20 rounded-lg">
              <GitBranch className="w-3 h-3 text-violet-400 animate-pulse" />
              <span className="text-xs text-violet-400 font-medium">Ref analysis</span>
            </div>
          )}
          {!isIndexing && !isSummarizing && !isReferencing && s.activeProjectId && (
            <div className="flex items-center gap-2 px-3 py-1.5 bg-zinc-800 border border-zinc-700 rounded-lg">
              <span className="w-2 h-2 rounded-full bg-zinc-400" />
              <span className="text-xs text-zinc-400 font-medium">Idle</span>
            </div>
          )}
          {!s.activeProjectId && (
            <div className="flex items-center gap-2 px-3 py-1.5 bg-zinc-800 border border-zinc-700 rounded-lg">
              <span className="w-2 h-2 rounded-full bg-zinc-600" />
              <span className="text-xs text-zinc-500 font-medium">No active workspace</span>
            </div>
          )}
        </div>
      </div>

      {/* Active workspace + progress */}
      <div className="bg-zinc-900 border border-zinc-800 rounded-xl p-4 space-y-3">
        <div className="flex items-center justify-between">
          <span className="text-xs font-medium text-zinc-400">Active Workspace</span>
          {s.activeProjectId && (
            <button
              onClick={() => deactivateMutation.mutate()}
              disabled={deactivateMutation.isPending}
              className="text-xs text-zinc-500 hover:text-zinc-300 transition-colors"
            >
              Deactivate
            </button>
          )}
        </div>

        {s.activeProjectId ? (
          <div className="flex items-center gap-2">
            <span className="text-sm font-medium text-zinc-100">{s.activeProjectName}</span>
            <Link
              to={`/projects/${s.activeProjectId}`}
              className="flex items-center gap-0.5 text-xs text-indigo-400 hover:text-indigo-300 transition-colors"
            >
              View <ChevronRight className="w-3 h-3" />
            </Link>
          </div>
        ) : (
          <div className="space-y-2">
            <p className="text-sm text-zinc-500">No workspace is active. Select one to start indexing:</p>
            <div className="flex flex-wrap gap-2">
              {projects?.map(p => (
                <button
                  key={p.id}
                  onClick={() => activateMutation.mutate(p.id)}
                  disabled={activateMutation.isPending}
                  className="flex items-center gap-1.5 px-3 py-1.5 bg-indigo-600 hover:bg-indigo-500 disabled:opacity-40 text-white text-xs font-medium rounded-lg transition-colors"
                >
                  {activateMutation.isPending ? <Loader2 className="w-3 h-3 animate-spin" /> : null}
                  {p.name}
                </button>
              ))}
            </div>
          </div>
        )}

        {s.activeProjectId && (
          <div className="space-y-2.5">
            {/* Ingestion progress */}
            <div className="space-y-1.5">
              <div className="flex items-center justify-between text-xs text-zinc-500">
                <span>Indexing progress</span>
                <span className="tabular-nums">{doneCount} / {s.totalIndexableFiles}</span>
              </div>
              <ProgressBar value={doneCount} max={s.totalIndexableFiles} color="bg-blue-500" />
            </div>

            {/* Live activity lines */}
            {(s.currentFile || s.currentSummaryFile || s.currentReferenceFile) && (
              <div className="space-y-1 pt-0.5">
                {s.currentFile && (
                  <div className="flex items-center gap-2 text-xs">
                    <Loader2 className="w-3 h-3 text-blue-400 animate-spin flex-shrink-0" />
                    <span className="text-zinc-500 w-20 flex-shrink-0">Indexing</span>
                    <span className="font-mono text-zinc-300 truncate">{s.currentFile}</span>
                  </div>
                )}
                {s.currentSummaryFile && (
                  <div className="flex items-center gap-2 text-xs">
                    <Sparkles className="w-3 h-3 text-indigo-400 flex-shrink-0" />
                    <span className="text-zinc-500 w-20 flex-shrink-0">Summarizing</span>
                    <span className="font-mono text-zinc-300 truncate">{s.currentSummaryFile}</span>
                  </div>
                )}
                {s.currentReferenceFile && (
                  <div className="flex items-center gap-2 text-xs">
                    <GitBranch className="w-3 h-3 text-violet-400 flex-shrink-0" />
                    <span className="text-zinc-500 w-20 flex-shrink-0">Ref analysis</span>
                    <span className="font-mono text-zinc-300 truncate">{s.currentReferenceFile}</span>
                  </div>
                )}
              </div>
            )}
          </div>
        )}
      </div>

      {/* Sub-project breakdown */}
      {subProjectStatuses.length > 0 && s.activeProjectId && (
        <div className="space-y-2">
          <h2 className="text-sm font-medium text-zinc-300 flex items-center gap-2">
            <Layers className="w-3.5 h-3.5 text-zinc-500" />
            Sub-projects
          </h2>
          <div className="bg-zinc-900 border border-zinc-800 rounded-xl divide-y divide-zinc-800">
            {subProjectStatuses.map(sp => (
              <SubProjectRow key={sp.subProjectId} sp={sp} workspaceId={s.activeProjectId!} />
            ))}
          </div>
        </div>
      )}

      {/* Queue tabs */}
      {s.activeProjectId && (
        <div>
          {/* Tab bar */}
          <div className="flex border-b border-zinc-800">
            {tabs.map(tab => {
              const Icon = tab.icon
              const isActive = activeTab === tab.id
              return (
                <button
                  key={tab.id}
                  onClick={() => setActiveTab(tab.id)}
                  className={`flex items-center gap-2 px-4 py-2.5 text-xs font-medium border-b-2 transition-colors -mb-px ${
                    isActive
                      ? `${tab.activeColor} bg-zinc-900/50`
                      : 'text-zinc-500 border-transparent hover:text-zinc-300 hover:border-zinc-600'
                  }`}
                >
                  <Icon className="w-3.5 h-3.5" />
                  {tab.label}
                  {tab.count > 0 && (
                    <span className={`text-[10px] px-1.5 py-0.5 rounded-full tabular-nums ${
                      isActive ? 'bg-zinc-700 text-zinc-200' : 'bg-zinc-800 text-zinc-500'
                    }`}>
                      {tab.count}
                    </span>
                  )}
                </button>
              )
            })}
          </div>

          {/* Tab content */}
          <div className="bg-zinc-900 border border-t-0 border-zinc-800 rounded-b-xl min-h-[120px]">
            {activeTab === 'queue' && (
              <QueuePanel items={queuedIngestions} currentFile={s.currentFile} />
            )}
            {activeTab === 'summary' && (
              <SummaryPanel items={queuedSummaries} currentSummaryFile={s.currentSummaryFile} />
            )}
            {activeTab === 'references' && (
              <ReferencePanel
                currentReferenceFile={s.currentReferenceFile}
                depth={s.referenceQueueDepth ?? 0}
                totalSymbolReferences={s.totalSymbolReferences ?? 0}
              />
            )}
            {activeTab === 'indexed' && (
              <IndexedPanel jobs={s.recentJobs} />
            )}
            {activeTab === 'stale' && (
              staleQuery.isLoading
                ? <div className="flex items-center justify-center py-12"><Loader2 className="w-4 h-4 animate-spin text-zinc-500" /></div>
                : <StalePanel files={staleQuery.data ?? []} workspaceId={s.activeProjectId!} />
            )}
            {activeTab === 'errors' && (
              errorQuery.isLoading
                ? <div className="flex items-center justify-center py-12"><Loader2 className="w-4 h-4 animate-spin text-zinc-500" /></div>
                : <ErrorsPanel files={errorQuery.data ?? []} />
            )}
          </div>

          {activeTab === 'indexed' && s.recentJobs.length > 0 && (
            <p className="text-[10px] text-zinc-600 text-right pt-1.5 pr-1">
              Showing last {s.recentJobs.length} indexed files
            </p>
          )}
        </div>
      )}

      {/* Footer link */}
      {s.activeProjectId && (
        <div className="flex items-center justify-between text-xs text-zinc-600 pt-1">
          <div className="flex items-center gap-1">
            <FileText className="w-3 h-3" />
            <Link to={`/projects/${s.activeProjectId}`} className="hover:text-zinc-400 transition-colors">
              Browse all indexed files
            </Link>
          </div>
          <span className="tabular-nums">
            {s.indexedFiles} total &middot; {s.staleFiles} stale &middot; {s.errorFiles} errors
            {(s.totalSymbolReferences ?? 0) > 0 && (
              <span> &middot; {s.totalSymbolReferences!.toLocaleString()} sym refs</span>
            )}
          </span>
        </div>
      )}
    </div>
  )
}
