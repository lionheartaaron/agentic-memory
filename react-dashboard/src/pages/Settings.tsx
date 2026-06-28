import { useState } from 'react'
import { useQuery, useMutation, useQueryClient } from '@tanstack/react-query'
import {
  Settings2, Database, Brain, FolderGit2, Trash2,
  Loader2, AlertTriangle, CheckCircle2, RotateCcw,
} from 'lucide-react'
import { api } from '../api'

function fmtBytes(bytes: number) {
  if (bytes < 1024) return `${bytes} B`
  if (bytes < 1024 * 1024) return `${(bytes / 1024).toFixed(1)} KB`
  return `${(bytes / (1024 * 1024)).toFixed(1)} MB`
}

function StatChip({ label, value }: { label: string; value: string | number }) {
  return (
    <div className="flex flex-col gap-0.5">
      <span className="text-[10px] text-zinc-500 uppercase tracking-wider">{label}</span>
      <span className="text-sm font-semibold text-zinc-200 tabular-nums">{value}</span>
    </div>
  )
}

type ConfirmState = null | 'code-index' | 'memories' | 'workspaces' | 'full-reset'

function DangerButton({
  icon: Icon,
  label,
  description,
  confirmKey,
  activeConfirm,
  onRequest,
  onCancel,
  onConfirm,
  isPending,
  color = 'red',
}: {
  icon: React.ElementType
  label: string
  description: string
  confirmKey: ConfirmState
  activeConfirm: ConfirmState
  onRequest: () => void
  onCancel: () => void
  onConfirm: () => void
  isPending: boolean
  color?: 'red' | 'amber'
}) {
  const isActive = activeConfirm === confirmKey
  const borderColor = color === 'red' ? 'border-red-800/50' : 'border-amber-800/50'
  const bgColor = color === 'red' ? 'bg-red-950/20' : 'bg-amber-950/20'

  return (
    <div className={`border ${borderColor} ${bgColor} rounded-xl p-4 space-y-3`}>
      <div className="flex items-start justify-between gap-4">
        <div className="flex items-start gap-3 min-w-0">
          <div className={`w-8 h-8 rounded-lg flex items-center justify-center flex-shrink-0 ${
            color === 'red' ? 'bg-red-500/10' : 'bg-amber-500/10'
          }`}>
            <Icon className={`w-4 h-4 ${color === 'red' ? 'text-red-400' : 'text-amber-400'}`} />
          </div>
          <div className="min-w-0">
            <p className={`text-sm font-medium ${color === 'red' ? 'text-red-300' : 'text-amber-300'}`}>{label}</p>
            <p className="text-xs text-zinc-500 mt-0.5">{description}</p>
          </div>
        </div>

        {!isActive && (
          <button
            onClick={onRequest}
            className={`flex-shrink-0 px-3 py-1.5 text-xs font-medium rounded-lg border transition-colors ${
              color === 'red'
                ? 'border-red-700/50 text-red-400 hover:bg-red-900/30'
                : 'border-amber-700/50 text-amber-400 hover:bg-amber-900/30'
            }`}
          >
            {label}
          </button>
        )}
      </div>

      {isActive && (
        <div className={`flex items-center gap-3 pt-1 border-t ${color === 'red' ? 'border-red-900/50' : 'border-amber-900/50'}`}>
          <AlertTriangle className={`w-4 h-4 flex-shrink-0 ${color === 'red' ? 'text-red-400' : 'text-amber-400'}`} />
          <p className="text-xs text-zinc-400 flex-1">This cannot be undone. Are you sure?</p>
          <button
            onClick={onCancel}
            className="px-3 py-1.5 text-xs text-zinc-400 hover:text-zinc-200 transition-colors"
          >
            Cancel
          </button>
          <button
            onClick={onConfirm}
            disabled={isPending}
            className={`flex items-center gap-1.5 px-3 py-1.5 text-xs font-medium rounded-lg transition-colors disabled:opacity-40 ${
              color === 'red'
                ? 'bg-red-600 hover:bg-red-500 text-white'
                : 'bg-amber-600 hover:bg-amber-500 text-white'
            }`}
          >
            {isPending ? <Loader2 className="w-3 h-3 animate-spin" /> : <Trash2 className="w-3 h-3" />}
            Confirm
          </button>
        </div>
      )}
    </div>
  )
}

export default function Settings() {
  const queryClient = useQueryClient()
  const [confirm, setConfirm] = useState<ConfirmState>(null)
  const [lastAction, setLastAction] = useState<string | null>(null)

  const { data: stats, isLoading: statsLoading } = useQuery({
    queryKey: ['maintenance-stats'],
    queryFn: api.admin.maintenanceStats,
    refetchInterval: 15_000,
  })

  const done = (msg: string) => {
    setConfirm(null)
    setLastAction(msg)
    queryClient.invalidateQueries({ queryKey: ['maintenance-stats'] })
    queryClient.invalidateQueries({ queryKey: ['worker-status'] })
    queryClient.invalidateQueries({ queryKey: ['projects'] })
    setTimeout(() => setLastAction(null), 4000)
  }

  const clearCodeIndex = useMutation({
    mutationFn: api.admin.clearCodeIndex,
    onSuccess: () => done('Code index cleared.'),
  })

  const clearMemories = useMutation({
    mutationFn: api.admin.clearMemories,
    onSuccess: () => done('All memories deleted.'),
  })

  const clearWorkspaces = useMutation({
    mutationFn: api.admin.clearWorkspaces,
    onSuccess: () => done('All workspaces removed.'),
  })

  const fullReset = useMutation({
    mutationFn: api.admin.fullReset,
    onSuccess: () => done('Full reset complete.'),
  })

  const anyPending =
    clearCodeIndex.isPending ||
    clearMemories.isPending ||
    clearWorkspaces.isPending ||
    fullReset.isPending

  return (
    <div className="p-6 space-y-6 max-w-2xl">
      {/* Header */}
      <div className="flex items-center gap-3">
        <div className="w-10 h-10 rounded-xl bg-zinc-800 border border-zinc-700 flex items-center justify-center flex-shrink-0">
          <Settings2 className="w-5 h-5 text-zinc-300" />
        </div>
        <div>
          <h1 className="text-xl font-semibold text-zinc-100">Settings</h1>
          <p className="text-xs text-zinc-500 mt-0.5">System configuration and maintenance</p>
        </div>
      </div>

      {/* Success toast */}
      {lastAction && (
        <div className="flex items-center gap-2 px-4 py-3 bg-green-950/40 border border-green-800/50 rounded-xl text-sm text-green-400">
          <CheckCircle2 className="w-4 h-4 flex-shrink-0" />
          {lastAction}
        </div>
      )}

      {/* Stats */}
      <div className="bg-zinc-900 border border-zinc-800 rounded-xl p-4">
        <h2 className="text-xs font-medium text-zinc-400 uppercase tracking-wider mb-3">Current Data</h2>
        {statsLoading ? (
          <div className="flex items-center gap-2 text-zinc-600 text-sm">
            <Loader2 className="w-3.5 h-3.5 animate-spin" /> Loading…
          </div>
        ) : (
          <div className="grid grid-cols-2 sm:grid-cols-4 gap-4">
            <StatChip label="Memories" value={stats?.memories ?? 0} />
            <StatChip label="Indexed Files" value={stats?.codeIndexFiles ?? 0} />
            <StatChip label="Workspaces" value={stats?.workspaces ?? 0} />
            <StatChip label="DB Size" value={fmtBytes(stats?.dbSizeBytes ?? 0)} />
          </div>
        )}
      </div>

      {/* Maintenance */}
      <div className="space-y-3">
        <h2 className="text-sm font-medium text-zinc-300 flex items-center gap-2">
          <RotateCcw className="w-3.5 h-3.5 text-zinc-500" />
          Maintenance
        </h2>

        <DangerButton
          icon={Database}
          label="Clear Code Index"
          description="Deletes all indexed file records. Workspaces and memories are kept. Re-index to rebuild."
          confirmKey="code-index"
          activeConfirm={confirm}
          onRequest={() => setConfirm('code-index')}
          onCancel={() => setConfirm(null)}
          onConfirm={() => clearCodeIndex.mutate()}
          isPending={clearCodeIndex.isPending}
          color="amber"
        />

        <DangerButton
          icon={Brain}
          label="Clear Memories"
          description="Permanently deletes all stored memories. Workspaces and code index are kept."
          confirmKey="memories"
          activeConfirm={confirm}
          onRequest={() => setConfirm('memories')}
          onCancel={() => setConfirm(null)}
          onConfirm={() => clearMemories.mutate()}
          isPending={clearMemories.isPending}
          color="amber"
        />

        <DangerButton
          icon={FolderGit2}
          label="Clear Workspaces"
          description="Removes all registered workspaces. Code index records and memories are kept."
          confirmKey="workspaces"
          activeConfirm={confirm}
          onRequest={() => setConfirm('workspaces')}
          onCancel={() => setConfirm(null)}
          onConfirm={() => clearWorkspaces.mutate()}
          isPending={clearWorkspaces.isPending}
          color="amber"
        />
      </div>

      {/* Danger zone */}
      <div className="space-y-3">
        <h2 className="text-sm font-medium text-red-400 flex items-center gap-2">
          <AlertTriangle className="w-3.5 h-3.5" />
          Danger Zone
        </h2>

        <DangerButton
          icon={Trash2}
          label="Full Reset"
          description="Wipes everything: all memories, all indexed files, and all registered workspaces. The app starts from a clean state."
          confirmKey="full-reset"
          activeConfirm={confirm}
          onRequest={() => setConfirm('full-reset')}
          onCancel={() => setConfirm(null)}
          onConfirm={() => fullReset.mutate()}
          isPending={fullReset.isPending}
          color="red"
        />
      </div>

      {anyPending && (
        <div className="flex items-center gap-2 text-xs text-zinc-500">
          <Loader2 className="w-3 h-3 animate-spin" />
          Operation in progress…
        </div>
      )}
    </div>
  )
}
