import { useState } from 'react'
import { Link, useNavigate } from 'react-router-dom'
import { useQuery, useMutation, useQueryClient } from '@tanstack/react-query'
import {
  FolderGit2, Plus, Trash2, FolderOpen, X, Loader2, ChevronRight, Zap,
  RefreshCw, CheckCircle2,
} from 'lucide-react'
import { api } from '../api'
import { FileBrowser } from '../components/FileBrowser'
import type { WorkspaceRecord, SubProjectRecord } from '../types'

const TYPE_BADGE: Record<string, { label: string; cls: string }> = {
  CSharpProject: { label: 'C#', cls: 'bg-violet-500/15 text-violet-300 border-violet-500/25' },
  TypeScript:    { label: 'TS', cls: 'bg-blue-500/15 text-blue-300 border-blue-500/25' },
  Node:          { label: 'JS', cls: 'bg-yellow-500/15 text-yellow-300 border-yellow-500/25' },
  Python:        { label: 'PY', cls: 'bg-green-500/15 text-green-300 border-green-500/25' },
  Unknown:       { label: '?',  cls: 'bg-zinc-700/40 text-zinc-400 border-zinc-600/25' },
}

function SubProjectChip({ sp }: { sp: SubProjectRecord }) {
  const badge = TYPE_BADGE[sp.type] ?? TYPE_BADGE.Unknown
  return (
    <div className="flex items-center gap-1.5 min-w-0">
      <span className={`inline-flex items-center px-1.5 py-0.5 rounded text-[9px] font-bold border flex-shrink-0 ${badge.cls}`}>
        {badge.label}
      </span>
      <span className="text-xs text-zinc-400 truncate">{sp.name}</span>
      {sp.isProviderAvailable && (
        <span title="Provider available" className="flex-shrink-0">
          <CheckCircle2 className="w-3 h-3 text-green-400" />
        </span>
      )}
    </div>
  )
}

function AddWorkspaceModal({ onClose }: { onClose: () => void }) {
  const [name, setName] = useState('')
  const [rootPath, setRootPath] = useState('')
  const [browserOpen, setBrowserOpen] = useState(false)
  const [lastBrowsedPath, setLastBrowsedPath] = useState<string | undefined>(undefined)
  const [error, setError] = useState<string | null>(null)
  const queryClient = useQueryClient()

  const mutation = useMutation({
    mutationFn: () => api.workspaces.create(name.trim(), rootPath.trim()),
    onSuccess: () => {
      queryClient.invalidateQueries({ queryKey: ['projects'] })
      onClose()
    },
    onError: (e) => {
      setError(e instanceof Error ? e.message : 'Failed to create workspace.')
    },
  })

  return (
    <>
      {browserOpen && (
        <FileBrowser
          initialPath={lastBrowsedPath ?? (rootPath || undefined)}
          onSelectDirectory={path => {
            setRootPath(path)
            setBrowserOpen(false)
          }}
          onNavigate={path => setLastBrowsedPath(path)}
          onClose={() => setBrowserOpen(false)}
        />
      )}

      <div
        className="fixed inset-0 bg-black/60 flex items-center justify-center z-40"
        onClick={onClose}
      >
        <div
          className="bg-zinc-900 border border-zinc-700 rounded-xl w-[480px] p-6 shadow-2xl space-y-5"
          onClick={e => e.stopPropagation()}
        >
          <div className="flex items-center justify-between">
            <h2 className="text-base font-semibold text-zinc-100">Add Workspace</h2>
            <button onClick={onClose} className="text-zinc-500 hover:text-zinc-200 transition-colors">
              <X className="w-4 h-4" />
            </button>
          </div>

          <p className="text-xs text-zinc-500">
            Point to a project root and sub-projects (C#, TypeScript, Python) will be discovered automatically.
          </p>

          <div className="space-y-4">
            <div className="space-y-1.5">
              <label className="text-xs font-medium text-zinc-400">Workspace Name</label>
              <input
                type="text"
                value={name}
                onChange={e => setName(e.target.value)}
                onKeyDown={e => e.key === 'Enter' && !mutation.isPending && mutation.mutate()}
                placeholder="e.g. my-project"
                autoFocus
                className="w-full bg-zinc-800 border border-zinc-700 rounded-lg px-3 py-2 text-sm text-zinc-100 placeholder-zinc-500 focus:outline-none focus:ring-2 focus:ring-indigo-500 focus:border-transparent"
              />
            </div>

            <div className="space-y-1.5">
              <label className="text-xs font-medium text-zinc-400">Root Path</label>
              <div className="relative">
                <input
                  type="text"
                  value={rootPath}
                  onChange={e => setRootPath(e.target.value)}
                  placeholder="e.g. H:\DEV\my-project"
                  className="w-full bg-zinc-800 border border-zinc-700 rounded-lg pl-3 pr-10 py-2 text-sm text-zinc-100 placeholder-zinc-500 font-mono focus:outline-none focus:ring-2 focus:ring-indigo-500 focus:border-transparent"
                />
                <button
                  onClick={() => setBrowserOpen(true)}
                  title="Browse for folder"
                  className="absolute right-2 top-1/2 -translate-y-1/2 p-1.5 rounded text-zinc-500 hover:text-zinc-200 hover:bg-zinc-700 transition-colors"
                >
                  <FolderOpen className="w-4 h-4" />
                </button>
              </div>
            </div>

            {error && (
              <div className="text-sm text-red-400 bg-red-950/30 border border-red-800/50 rounded-lg px-3 py-2">
                {error}
              </div>
            )}
          </div>

          <div className="flex gap-3 justify-end pt-1">
            <button
              onClick={onClose}
              className="px-4 py-2 text-sm text-zinc-400 hover:text-zinc-200 transition-colors"
            >
              Cancel
            </button>
            <button
              onClick={() => mutation.mutate()}
              disabled={!name.trim() || !rootPath.trim() || mutation.isPending}
              className="flex items-center gap-2 px-4 py-2 bg-indigo-600 hover:bg-indigo-500 disabled:opacity-40 disabled:cursor-not-allowed text-white text-sm font-medium rounded-lg transition-colors"
            >
              {mutation.isPending ? (
                <Loader2 className="w-4 h-4 animate-spin" />
              ) : (
                <Plus className="w-4 h-4" />
              )}
              Add Workspace
            </button>
          </div>
        </div>
      </div>
    </>
  )
}

function WorkspaceCard({ workspace, activeProjectId }: { workspace: WorkspaceRecord; activeProjectId: string | null }) {
  const [confirmDelete, setConfirmDelete] = useState(false)
  const [showSubProjects, setShowSubProjects] = useState(false)
  const queryClient = useQueryClient()
  const navigate = useNavigate()
  const isActive = activeProjectId === workspace.id
  const subProjects = workspace.subProjects ?? []

  const deleteMutation = useMutation({
    mutationFn: () => api.workspaces.delete(workspace.id),
    onSuccess: () => queryClient.invalidateQueries({ queryKey: ['projects'] }),
  })

  const discoverMutation = useMutation({
    mutationFn: () => api.workspaces.discover(workspace.id),
    onSuccess: () => queryClient.invalidateQueries({ queryKey: ['projects'] }),
  })

  const activateMutation = useMutation({
    mutationFn: () => api.codeIndex.activate(workspace.id),
    onSuccess: () => {
      queryClient.invalidateQueries({ queryKey: ['worker-status'] })
      navigate('/worker')
    },
  })

  return (
    <div className={`bg-zinc-900 border rounded-xl overflow-hidden transition-colors group flex flex-col ${
      isActive ? 'border-green-500/40' : 'border-zinc-800 hover:border-zinc-700'
    }`}>
      {/* Card header */}
      <div className="p-4 flex flex-col gap-3 flex-1">
        <div className="flex items-start gap-3">
          <div className={`w-9 h-9 rounded-lg flex items-center justify-center flex-shrink-0 mt-0.5 ${
            isActive
              ? 'bg-green-500/10 border border-green-500/20'
              : 'bg-indigo-500/10 border border-indigo-500/20'
          }`}>
            <FolderGit2 className={`w-4 h-4 ${isActive ? 'text-green-400' : 'text-indigo-400'}`} />
          </div>
          <div className="flex-1 min-w-0">
            <div className="flex items-center gap-2">
              <h3 className="text-sm font-semibold text-zinc-200 truncate">{workspace.name}</h3>
              {isActive && (
                <span className="flex items-center gap-1 px-1.5 py-0.5 bg-green-500/10 border border-green-500/20 rounded text-[10px] text-green-400 font-medium flex-shrink-0">
                  <span className="w-1.5 h-1.5 rounded-full bg-green-400" />
                  Active
                </span>
              )}
            </div>
            <p className="text-xs text-zinc-500 font-mono truncate mt-0.5" title={workspace.rootPath}>
              {workspace.rootPath}
            </p>
          </div>
        </div>

        {/* Sub-projects summary / expand */}
        {subProjects.length > 0 && (
          <div>
            <button
              onClick={() => setShowSubProjects(v => !v)}
              className="flex items-center gap-1.5 text-xs text-zinc-500 hover:text-zinc-300 transition-colors"
            >
              <ChevronRight className={`w-3 h-3 transition-transform ${showSubProjects ? 'rotate-90' : ''}`} />
              {subProjects.length} sub-project{subProjects.length !== 1 ? 's' : ''}
            </button>

            {showSubProjects && (
              <div className="mt-2 space-y-1.5 pl-4 border-l border-zinc-800">
                {subProjects.map(sp => (
                  <SubProjectChip key={sp.id} sp={sp} />
                ))}
              </div>
            )}

            {!showSubProjects && (
              <div className="flex flex-wrap gap-1 mt-1.5 pl-4">
                {subProjects.slice(0, 4).map(sp => {
                  const badge = TYPE_BADGE[sp.type] ?? TYPE_BADGE.Unknown
                  return (
                    <span
                      key={sp.id}
                      className={`inline-flex items-center px-1.5 py-0.5 rounded text-[9px] font-bold border ${badge.cls}`}
                      title={sp.name}
                    >
                      {badge.label}
                    </span>
                  )
                })}
                {subProjects.length > 4 && (
                  <span className="text-[10px] text-zinc-600 self-center">+{subProjects.length - 4}</span>
                )}
              </div>
            )}
          </div>
        )}
      </div>

      {/* Card footer */}
      <div className="px-4 py-2.5 border-t border-zinc-800">
        {confirmDelete ? (
          <div className="flex items-center gap-2 w-full">
            <span className="text-xs text-zinc-400 flex-1">Remove this workspace?</span>
            <button
              onClick={() => deleteMutation.mutate()}
              disabled={deleteMutation.isPending}
              className="text-xs text-red-400 hover:text-red-300 transition-colors font-medium"
            >
              {deleteMutation.isPending ? 'Removing…' : 'Remove'}
            </button>
            <button
              onClick={() => setConfirmDelete(false)}
              className="text-xs text-zinc-500 hover:text-zinc-300 transition-colors"
            >
              Cancel
            </button>
          </div>
        ) : (
          <div className="flex items-center gap-3 w-full">
            <Link
              to={`/projects/${workspace.id}`}
              className="flex items-center gap-1 text-xs text-indigo-400 hover:text-indigo-300 transition-colors font-medium"
            >
              Open <ChevronRight className="w-3 h-3" />
            </Link>
            {!isActive && (
              <button
                onClick={() => activateMutation.mutate()}
                disabled={activateMutation.isPending}
                className="flex items-center gap-1 text-xs text-green-400 hover:text-green-300 transition-colors font-medium disabled:opacity-40"
              >
                {activateMutation.isPending
                  ? <Loader2 className="w-3 h-3 animate-spin" />
                  : <Zap className="w-3 h-3" />}
                Activate
              </button>
            )}
            <button
              onClick={() => discoverMutation.mutate()}
              disabled={discoverMutation.isPending}
              className="flex items-center gap-1 text-xs text-zinc-500 hover:text-zinc-300 transition-colors font-medium disabled:opacity-40"
              title="Re-discover sub-projects"
            >
              {discoverMutation.isPending
                ? <Loader2 className="w-3 h-3 animate-spin" />
                : <RefreshCw className="w-3 h-3" />}
            </button>
            <button
              onClick={() => setConfirmDelete(true)}
              className="ml-auto opacity-0 group-hover:opacity-100 text-zinc-600 hover:text-red-400 transition-all p-1 rounded"
              title="Remove workspace"
            >
              <Trash2 className="w-3.5 h-3.5" />
            </button>
          </div>
        )}
      </div>
    </div>
  )
}

export default function Projects() {
  const [showAdd, setShowAdd] = useState(false)

  const { data: projects, isLoading } = useQuery({
    queryKey: ['projects'],
    queryFn: api.projects.list,
  })

  const { data: workerStatus } = useQuery({
    queryKey: ['worker-status'],
    queryFn: api.codeIndex.workerStatus,
    refetchInterval: 10_000,
    retry: false,
  })

  const activeProjectId = workerStatus?.activeProjectId ?? null

  return (
    <>
      {showAdd && <AddWorkspaceModal onClose={() => setShowAdd(false)} />}

      <div className="p-8 max-w-5xl mx-auto space-y-8">
        <div className="flex items-start justify-between">
          <div>
            <div className="flex items-center gap-3 mb-1">
              <FolderGit2 className="w-6 h-6 text-indigo-400" />
              <h1 className="text-2xl font-semibold text-zinc-100">Workspaces</h1>
            </div>
            <p className="text-sm text-zinc-400">
              Register workspace roots to discover sub-projects and enable code intelligence.
              Activate a workspace to start background indexing.
            </p>
          </div>
          <button
            onClick={() => setShowAdd(true)}
            className="flex items-center gap-2 px-3 py-1.5 bg-indigo-500 hover:bg-indigo-600 text-white rounded-lg text-sm font-medium transition-colors"
          >
            <Plus className="w-4 h-4" />
            Add Workspace
          </button>
        </div>

        {isLoading ? (
          <div className="flex items-center justify-center py-16">
            <Loader2 className="w-5 h-5 animate-spin text-zinc-500" />
          </div>
        ) : !projects?.length ? (
          <div className="py-24 text-center">
            <FolderGit2 className="w-10 h-10 text-zinc-700 mx-auto mb-3" />
            <p className="text-zinc-500 text-sm">No workspaces yet</p>
            <button
              onClick={() => setShowAdd(true)}
              className="mt-3 text-indigo-400 hover:text-indigo-300 text-sm transition-colors"
            >
              Add your first workspace →
            </button>
          </div>
        ) : (
          <div className="grid grid-cols-1 sm:grid-cols-2 lg:grid-cols-3 gap-4">
            {projects.map(p => (
              <WorkspaceCard key={p.id} workspace={p} activeProjectId={activeProjectId} />
            ))}
          </div>
        )}
      </div>
    </>
  )
}
