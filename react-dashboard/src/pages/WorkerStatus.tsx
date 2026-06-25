import { useQuery, useMutation, useQueryClient } from '@tanstack/react-query'
import { Link } from 'react-router-dom'
import {
  Activity, CheckCircle2, XCircle, Clock, Loader2,
  ChevronRight, RefreshCw,
} from 'lucide-react'
import { api } from '../api'

function ProgressBar({ value, max }: { value: number; max: number }) {
  const pct = max > 0 ? Math.min(100, Math.round((value / max) * 100)) : 0
  return (
    <div className="flex items-center gap-3">
      <div className="flex-1 h-2 bg-zinc-800 rounded-full overflow-hidden">
        <div
          className="h-full bg-indigo-500 rounded-full transition-all duration-500"
          style={{ width: `${pct}%` }}
        />
      </div>
      <span className="text-xs text-zinc-400 tabular-nums w-24 text-right flex-shrink-0">
        {value} / {max} files
      </span>
    </div>
  )
}

function StatChip({ label, value, highlight }: { label: string; value: number; highlight?: string }) {
  const base = 'flex flex-col items-center px-4 py-3 rounded-lg border'
  const color = highlight ?? 'border-zinc-800 bg-zinc-900/50'
  return (
    <div className={`${base} ${color}`}>
      <span className="text-lg font-semibold text-zinc-100 tabular-nums">{value}</span>
      <span className="text-xs text-zinc-500 mt-0.5">{label}</span>
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

const LANG_SHORT: Record<string, string> = {
  csharp: 'cs', typescript: 'ts', javascript: 'js',
  python: 'py', go: 'go', rust: 'rs', unknown: '?',
}

export default function WorkerStatus() {
  const queryClient = useQueryClient()

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

  const deactivateMutation = useMutation({
    mutationFn: api.codeIndex.deactivate,
    onSuccess: () => queryClient.invalidateQueries({ queryKey: ['worker-status'] }),
  })

  const activateMutation = useMutation({
    mutationFn: (id: string) => api.codeIndex.activate(id),
    onSuccess: () => queryClient.invalidateQueries({ queryKey: ['worker-status'] }),
  })

  if (isLoading) {
    return (
      <div className="flex items-center justify-center h-64">
        <Loader2 className="w-5 h-5 animate-spin text-zinc-500" />
      </div>
    )
  }

  const s = status!
  const doneCount = s.indexedFiles - s.staleFiles

  return (
    <div className="p-8 max-w-3xl mx-auto space-y-6">
      {/* Header */}
      <div className="flex items-center justify-between">
        <div className="flex items-center gap-3">
          <div className="w-10 h-10 rounded-xl bg-indigo-500/10 border border-indigo-500/20 flex items-center justify-center flex-shrink-0">
            <Activity className="w-5 h-5 text-indigo-400" />
          </div>
          <div>
            <h1 className="text-xl font-semibold text-zinc-100">Code Index Worker</h1>
            <p className="text-xs text-zinc-500 mt-0.5">Background ingestion status</p>
          </div>
        </div>

        {s.isProcessing ? (
          <div className="flex items-center gap-2 px-3 py-1.5 bg-green-500/10 border border-green-500/20 rounded-lg">
            <span className="w-2 h-2 rounded-full bg-green-400 animate-pulse" />
            <span className="text-xs text-green-400 font-medium">Processing</span>
          </div>
        ) : s.activeProjectId ? (
          <div className="flex items-center gap-2 px-3 py-1.5 bg-zinc-800 border border-zinc-700 rounded-lg">
            <span className="w-2 h-2 rounded-full bg-zinc-400" />
            <span className="text-xs text-zinc-400 font-medium">Idle</span>
          </div>
        ) : (
          <div className="flex items-center gap-2 px-3 py-1.5 bg-zinc-800 border border-zinc-700 rounded-lg">
            <span className="w-2 h-2 rounded-full bg-zinc-600" />
            <span className="text-xs text-zinc-500 font-medium">No active project</span>
          </div>
        )}
      </div>

      {/* Active project */}
      <div className="bg-zinc-900 border border-zinc-800 rounded-xl p-4 space-y-3">
        <div className="flex items-center justify-between">
          <span className="text-xs font-medium text-zinc-400">Active Project</span>
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
            <p className="text-sm text-zinc-500">No project is active. Select one to start indexing:</p>
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
          <>
            {s.isProcessing && s.currentFile && (
              <p className="text-xs text-zinc-500 font-mono truncate">
                Indexing: <span className="text-zinc-300">{s.currentFile}</span>
              </p>
            )}
            <ProgressBar value={doneCount} max={s.totalIndexableFiles} />
          </>
        )}
      </div>

      {/* Stats */}
      {s.activeProjectId && (
        <div className="grid grid-cols-5 gap-3">
          <StatChip label="Queue" value={s.queueDepth} />
          <StatChip
            label="Summary"
            value={s.summaryQueueDepth}
            highlight={s.summaryQueueDepth > 0 ? 'border-indigo-800/50 bg-indigo-950/20' : undefined}
          />
          <StatChip label="Indexed" value={s.indexedFiles} />
          <StatChip
            label="Stale"
            value={s.staleFiles}
            highlight={s.staleFiles > 0 ? 'border-amber-800/50 bg-amber-950/20' : undefined}
          />
          <StatChip
            label="Errors"
            value={s.errorFiles}
            highlight={s.errorFiles > 0 ? 'border-red-800/50 bg-red-950/20' : undefined}
          />
        </div>
      )}

      {/* Recent jobs */}
      {s.recentJobs.length > 0 && (
        <div className="space-y-2">
          <h2 className="text-sm font-medium text-zinc-300 flex items-center gap-2">
            <RefreshCw className="w-3.5 h-3.5 text-zinc-500" />
            Recent
          </h2>
          <div className="bg-zinc-900 border border-zinc-800 rounded-xl divide-y divide-zinc-800">
            {s.recentJobs.map((j, i) => (
              <div key={i} className="flex items-center gap-3 px-4 py-2.5">
                <CheckCircle2 className="w-3.5 h-3.5 text-green-400 flex-shrink-0" />
                <span className="flex-1 min-w-0 font-mono text-xs text-zinc-300 truncate">
                  {j.relativePath}
                </span>
                <span className="text-[10px] text-zinc-600 bg-zinc-800 px-1.5 py-0.5 rounded flex-shrink-0">
                  {LANG_SHORT[j.language] ?? j.language}
                </span>
                <span className="text-xs text-zinc-500 flex-shrink-0 w-14 text-right">
                  {j.symbolCount} sym
                </span>
                <span className="text-xs text-zinc-600 flex-shrink-0 w-16 text-right tabular-nums">
                  {j.durationMs}ms
                </span>
                <span className="text-xs text-zinc-600 flex-shrink-0 w-16 text-right">
                  {timeAgo(j.indexedAt)}
                </span>
              </div>
            ))}
          </div>
        </div>
      )}

      {/* Errors */}
      {s.recentErrors.length > 0 && (
        <div className="space-y-2">
          <h2 className="text-sm font-medium text-red-400 flex items-center gap-2">
            <XCircle className="w-3.5 h-3.5" />
            Errors
          </h2>
          <div className="bg-red-950/20 border border-red-800/50 rounded-xl divide-y divide-red-900/40">
            {s.recentErrors.map((e, i) => (
              <div key={i} className="flex items-start gap-3 px-4 py-2.5">
                <XCircle className="w-3.5 h-3.5 text-red-400 flex-shrink-0 mt-0.5" />
                <div className="flex-1 min-w-0">
                  <p className="font-mono text-xs text-zinc-300 truncate">{e.relativePath}</p>
                  <p className="text-xs text-red-400 mt-0.5">{e.error}</p>
                </div>
                <span className="text-xs text-zinc-600 flex-shrink-0">
                  <Clock className="w-3 h-3 inline mr-1" />{timeAgo(e.occurredAt)}
                </span>
              </div>
            ))}
          </div>
        </div>
      )}
    </div>
  )
}
