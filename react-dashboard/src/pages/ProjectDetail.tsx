import { useState } from 'react'
import { useParams, Link, useNavigate } from 'react-router-dom'
import { useQuery, useMutation, useQueryClient } from '@tanstack/react-query'
import { FolderGit2, ChevronRight, FileCode, Loader2, Database, Zap, Activity } from 'lucide-react'
import { api } from '../api'
import { FileSummaryTool } from '../components/FileSummaryTool'
import { FilesIndex } from '../components/FilesIndex'

type ToolTab = { id: string; label: string; icon: React.ElementType }

const TOOL_TABS: ToolTab[] = [
  { id: 'files', label: 'Files', icon: Database },
  { id: 'file-summary', label: 'File Summary', icon: FileCode },
]

export default function ProjectDetail() {
  const { id } = useParams<{ id: string }>()
  const [activeTab, setActiveTab] = useState('files')
  const navigate = useNavigate()
  const queryClient = useQueryClient()

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
          Projects
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

      {/* Tab content */}
      {activeTab === 'files' && <FilesIndex projectId={id!} />}
      {activeTab === 'file-summary' && (
        <FileSummaryTool defaultPath={project.rootPath} />
      )}
    </div>
  )
}
