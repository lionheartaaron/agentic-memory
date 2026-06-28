import { useParams, Link, useNavigate, useSearchParams } from 'react-router-dom'
import { useQuery, useMutation, useQueryClient } from '@tanstack/react-query'
import {
  FolderGit2, ChevronRight, FileCode, Loader2,
  Database, Zap, Activity, Code2, Sparkles,
} from 'lucide-react'
import { api } from '../api'
import { FileSummaryTool } from '../components/FileSummaryTool'
import { FilesIndex } from '../components/FilesIndex'
import { SymbolsIndex } from '../components/SymbolsIndex'
import { CodeIntelligence } from '../components/CodeIntelligence'
import type { SubProjectRecord } from '../types'

type ToolTab = { id: string; label: string; icon: React.ElementType }

const TOOL_TABS: ToolTab[] = [
  { id: 'intelligence', label: 'Intelligence', icon: Sparkles  },
  { id: 'files',        label: 'Files',        icon: Database  },
  { id: 'symbols',      label: 'Symbols',      icon: Code2     },
  { id: 'file-summary', label: 'File Summary', icon: FileCode  },
]

const TYPE_BADGE: Record<string, { label: string; cls: string }> = {
  CSharpProject: { label: 'C#', cls: 'bg-violet-500/15 text-violet-300 border-violet-500/25' },
  TypeScript:    { label: 'TS', cls: 'bg-blue-500/15 text-blue-300 border-blue-500/25' },
  Node:          { label: 'JS', cls: 'bg-yellow-500/15 text-yellow-300 border-yellow-500/25' },
  Python:        { label: 'PY', cls: 'bg-green-500/15 text-green-300 border-green-500/25' },
  Unknown:       { label: '?',  cls: 'bg-zinc-700/40 text-zinc-400 border-zinc-600/25' },
}

function SubProjectPill({
  sp,
  active,
  onClick,
}: {
  sp: SubProjectRecord | null
  active: boolean
  onClick: () => void
}) {
  if (sp === null) {
    return (
      <button
        onClick={onClick}
        className={`flex items-center gap-1.5 px-2.5 py-1 rounded-full text-xs font-medium border transition-colors ${
          active
            ? 'bg-indigo-500/20 border-indigo-500/40 text-indigo-300'
            : 'bg-zinc-800 border-zinc-700 text-zinc-400 hover:text-zinc-200 hover:border-zinc-600'
        }`}
      >
        All
      </button>
    )
  }

  const badge = TYPE_BADGE[sp.type] ?? TYPE_BADGE.Unknown
  return (
    <button
      onClick={onClick}
      className={`flex items-center gap-1.5 px-2.5 py-1 rounded-full text-xs font-medium border transition-colors ${
        active
          ? 'bg-indigo-500/20 border-indigo-500/40 text-indigo-300'
          : 'bg-zinc-800 border-zinc-700 text-zinc-400 hover:text-zinc-200 hover:border-zinc-600'
      }`}
    >
      <span className={`inline-flex items-center px-1 py-0 rounded text-[9px] font-bold border ${badge.cls}`}>
        {badge.label}
      </span>
      {sp.name}
    </button>
  )
}

export default function ProjectDetail() {
  const { id } = useParams<{ id: string }>()
  const [searchParams, setSearchParams] = useSearchParams()
  const navigate = useNavigate()
  const queryClient = useQueryClient()

  const activeTab          = searchParams.get('tab') ?? 'files'
  const activeSubProjectId = searchParams.get('sub') ?? undefined

  const setActiveTab = (tab: string) => {
    setSearchParams(prev => {
      const next = new URLSearchParams(prev)
      next.set('tab', tab)
      next.delete('expandFile')
      return next
    }, { replace: true })
  }

  const setActiveSubProjectId = (subId: string | undefined) => {
    setSearchParams(prev => {
      const next = new URLSearchParams(prev)
      if (subId) next.set('sub', subId)
      else next.delete('sub')
      return next
    }, { replace: true })
  }

  // Called from SymbolsIndex or FilesIndex "Go to file" buttons
  const onNavigateToFile = (fileId: string) => {
    setSearchParams({ tab: 'files', expandFile: fileId }, { replace: true })
  }

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

  const activateMutation = useMutation({
    mutationFn: () => api.codeIndex.activate(id!),
    onSuccess: () => {
      queryClient.invalidateQueries({ queryKey: ['worker-status'] })
      navigate('/worker')
    },
  })

  const project = projects?.find(p => p.id === id)
  const isActive = workerStatus?.activeProjectId === id
  const subProjects = project?.subProjects ?? []

  if (isLoading) {
    return (
      <div className="flex items-center justify-center h-64">
        <Loader2 className="w-5 h-5 animate-spin text-zinc-500" />
      </div>
    )
  }

  if (!project) {
    return (
      <div className="p-8 text-center space-y-3">
        <p className="text-zinc-500 text-sm">Project not found.</p>
        <Link to="/projects" className="inline-block text-indigo-400 hover:text-indigo-300 text-sm transition-colors">
          ← Back to Projects
        </Link>
      </div>
    )
  }

  return (
    <div className="p-6 space-y-6">
      {/* Breadcrumb */}
      <nav className="flex items-center gap-1.5 text-sm">
        <Link to="/projects" className="text-zinc-500 hover:text-zinc-300 transition-colors">
          Workspaces
        </Link>
        <ChevronRight className="w-3.5 h-3.5 text-zinc-700" />
        <span className="text-zinc-200">{project.name}</span>
      </nav>

      {/* Header */}
      <div className="flex items-center gap-3">
        <div className={`w-10 h-10 rounded-xl flex items-center justify-center flex-shrink-0 ${
          isActive
            ? 'bg-green-500/10 border border-green-500/20'
            : 'bg-indigo-500/10 border border-indigo-500/20'
        }`}>
          <FolderGit2 className={`w-5 h-5 ${isActive ? 'text-green-400' : 'text-indigo-400'}`} />
        </div>
        <div className="flex-1 min-w-0">
          <div className="flex items-center gap-2 flex-wrap">
            <h1 className="text-xl font-semibold text-zinc-100">{project.name}</h1>
            {subProjects.length > 0 && (
              <span className="text-xs text-zinc-500 bg-zinc-800 border border-zinc-700 px-1.5 py-0.5 rounded">
                {subProjects.length} sub-project{subProjects.length !== 1 ? 's' : ''}
              </span>
            )}
            {isActive ? (
              <Link
                to="/worker"
                className="flex items-center gap-1 px-2 py-0.5 bg-green-500/10 border border-green-500/20 rounded-full text-xs text-green-400 font-medium hover:bg-green-500/20 transition-colors"
              >
                <Activity className="w-3 h-3" />
                Active
              </Link>
            ) : (
              <button
                onClick={() => activateMutation.mutate()}
                disabled={activateMutation.isPending}
                className="flex items-center gap-1 px-2 py-0.5 bg-zinc-800 border border-zinc-700 rounded-full text-xs text-zinc-400 hover:text-green-400 hover:border-green-500/40 transition-colors disabled:opacity-40"
              >
                {activateMutation.isPending
                  ? <Loader2 className="w-3 h-3 animate-spin" />
                  : <Zap className="w-3 h-3" />}
                Activate
              </button>
            )}
          </div>
          <p className="text-xs text-zinc-500 font-mono mt-0.5">{project.rootPath}</p>
        </div>
      </div>

      {/* Tool tabs */}
      <div className="border-b border-zinc-800">
        <div className="flex gap-1">
          {TOOL_TABS.map(tab => {
            const Icon = tab.icon
            return (
              <button
                key={tab.id}
                onClick={() => setActiveTab(tab.id)}
                className={`flex items-center gap-2 px-4 py-2.5 text-sm font-medium border-b-2 transition-colors -mb-px ${
                  activeTab === tab.id
                    ? 'border-indigo-500 text-indigo-400'
                    : 'border-transparent text-zinc-500 hover:text-zinc-300'
                }`}
              >
                <Icon className="w-4 h-4" />
                {tab.label}
              </button>
            )
          })}
        </div>
      </div>

      {/* Sub-project filter — shown on files, symbols and intelligence tabs */}
      {(activeTab === 'files' || activeTab === 'symbols' || activeTab === 'intelligence') && subProjects.length > 1 && (
        <div className="flex flex-wrap items-center gap-2">
          <span className="text-xs text-zinc-500 mr-1">Filter:</span>
          <SubProjectPill
            sp={null}
            active={activeSubProjectId === undefined}
            onClick={() => setActiveSubProjectId(undefined)}
          />
          {subProjects.map(sp => (
            <SubProjectPill
              key={sp.id}
              sp={sp}
              active={activeSubProjectId === sp.id}
              onClick={() => setActiveSubProjectId(sp.id)}
            />
          ))}
        </div>
      )}

      {/* Tab content */}
      {activeTab === 'intelligence' && (
        <CodeIntelligence
          projectId={id!}
          subProjectId={activeSubProjectId}
          onNavigateToFile={onNavigateToFile}
        />
      )}
      {activeTab === 'files' && (
        <FilesIndex
          projectId={id!}
          subProjectId={activeSubProjectId}
          onNavigateToFile={onNavigateToFile}
        />
      )}
      {activeTab === 'symbols' && (
        <SymbolsIndex
          projectId={id!}
          subProjectId={activeSubProjectId}
          onNavigateToFile={onNavigateToFile}
        />
      )}
      {activeTab === 'file-summary' && (
        <FileSummaryTool defaultPath={project.rootPath} />
      )}
    </div>
  )
}
