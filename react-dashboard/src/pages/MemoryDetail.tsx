import { useState } from 'react'
import { useParams, useNavigate, Link } from 'react-router-dom'
import { useQuery, useMutation, useQueryClient } from '@tanstack/react-query'
import { ChevronLeft, Save, Trash2, Pin, ExternalLink } from 'lucide-react'
import { api } from '../api'
import { getCurrentStrength, strengthColor, strengthBg, timeAgo } from '../utils'
import type { UpdateMemoryRequest } from '../types'

export default function MemoryDetail() {
  const { id } = useParams<{ id: string }>()
  const navigate = useNavigate()
  const queryClient = useQueryClient()

  const { data: memory, isLoading, isError } = useQuery({
    queryKey: ['memory', id],
    queryFn: () => api.get(id!),
    enabled: !!id,
  })

  const [edits, setEdits] = useState<UpdateMemoryRequest>({})
  const [hasChanges, setHasChanges] = useState(false)
  const [confirmDelete, setConfirmDelete] = useState(false)
  const [tagsInput, setTagsInput] = useState<string | null>(null)

  const updateMutation = useMutation({
    mutationFn: (data: UpdateMemoryRequest) => api.update(id!, data),
    onSuccess: (updated) => {
      queryClient.setQueryData(['memory', id], updated)
      queryClient.invalidateQueries({ queryKey: ['memories'] })
      setEdits({})
      setHasChanges(false)
    },
  })

  const deleteMutation = useMutation({
    mutationFn: () => api.delete(id!),
    onSuccess: () => {
      queryClient.invalidateQueries({ queryKey: ['memories'] })
      queryClient.invalidateQueries({ queryKey: ['stats'] })
      navigate('/memories')
    },
  })

  const set = (field: keyof UpdateMemoryRequest, value: string | string[]) => {
    setEdits((prev) => ({ ...prev, [field]: value }))
    setHasChanges(true)
  }

  const handleSave = () => {
    const payload = { ...edits }
    if (tagsInput !== null) {
      payload.tags = tagsInput
        .split(',')
        .map((t) => t.trim())
        .filter(Boolean)
    }
    updateMutation.mutate(payload)
    setTagsInput(null)
  }

  if (isLoading) {
    return (
      <div className="p-6 max-w-5xl mx-auto animate-pulse space-y-5">
        <div className="h-5 bg-zinc-800 rounded w-24" />
        <div className="h-7 bg-zinc-800 rounded w-1/2" />
        <div className="h-4 bg-zinc-800 rounded w-3/4" />
        <div className="h-48 bg-zinc-800 rounded" />
      </div>
    )
  }

  if (isError || !memory) {
    return (
      <div className="p-6 text-center text-zinc-500">
        Memory not found.{' '}
        <Link to="/memories" className="text-indigo-400 hover:text-indigo-300">
          Back to memories
        </Link>
      </div>
    )
  }

  const strength = getCurrentStrength(memory)
  const title = edits.title ?? memory.title
  const summary = edits.summary ?? memory.summary
  const content = edits.content ?? memory.content
  const displayedTags = tagsInput !== null
    ? tagsInput.split(',').map((t) => t.trim()).filter(Boolean)
    : (edits.tags ?? memory.tags)

  const metaRows: Array<{ label: string; value: string }> = [
    { label: 'Created', value: new Date(memory.createdAt).toLocaleString() },
    { label: 'Last accessed', value: timeAgo(memory.lastAccessedAt) },
    { label: 'Access count', value: `${memory.accessCount}×` },
    { label: 'Importance', value: memory.importance.toFixed(2) },
    { label: 'Decay rate', value: `${memory.decayRate.toFixed(3)}/day` },
    { label: 'Base strength', value: memory.baseStrength.toFixed(3) },
  ]

  return (
    <div className="p-6 max-w-5xl mx-auto">
      {/* Header */}
      <div className="flex items-center justify-between mb-6">
        <Link
          to="/memories"
          className="flex items-center gap-1.5 text-sm text-zinc-500 hover:text-zinc-300 transition-colors"
        >
          <ChevronLeft className="w-4 h-4" />
          Memories
        </Link>

        <div className="flex items-center gap-2">
          {memory.isPinned && (
            <span className="flex items-center gap-1 text-xs text-indigo-400 px-2 py-1 bg-indigo-500/10 rounded-lg">
              <Pin className="w-3 h-3" />
              Pinned
            </span>
          )}
          {hasChanges && (
            <button
              onClick={handleSave}
              disabled={updateMutation.isPending}
              className="flex items-center gap-1.5 px-3 py-1.5 bg-indigo-500 hover:bg-indigo-600 disabled:opacity-50 text-white rounded-lg text-sm font-medium transition-colors"
            >
              <Save className="w-3.5 h-3.5" />
              {updateMutation.isPending ? 'Saving…' : 'Save'}
            </button>
          )}
          {confirmDelete ? (
            <div className="flex items-center gap-2">
              <span className="text-xs text-red-400">Delete?</span>
              <button
                onClick={() => deleteMutation.mutate()}
                disabled={deleteMutation.isPending}
                className="px-2.5 py-1 bg-red-500 hover:bg-red-600 text-white rounded-lg text-xs font-medium disabled:opacity-50"
              >
                Confirm
              </button>
              <button
                onClick={() => setConfirmDelete(false)}
                className="px-2.5 py-1 bg-zinc-800 text-zinc-400 hover:text-zinc-200 rounded-lg text-xs transition-colors"
              >
                Cancel
              </button>
            </div>
          ) : (
            <button
              onClick={() => setConfirmDelete(true)}
              className="flex items-center gap-1.5 px-3 py-1.5 text-zinc-500 hover:text-red-400 hover:bg-red-400/10 rounded-lg text-sm transition-colors"
            >
              <Trash2 className="w-3.5 h-3.5" />
              Delete
            </button>
          )}
        </div>
      </div>

      {updateMutation.isError && (
        <div className="mb-4 px-3 py-2 bg-red-500/10 border border-red-500/20 rounded-lg text-xs text-red-400">
          {(updateMutation.error as Error).message}
        </div>
      )}

      <div className="grid grid-cols-1 lg:grid-cols-3 gap-6">
        {/* Main content */}
        <div className="lg:col-span-2 space-y-4">
          <input
            value={title}
            onChange={(e) => set('title', e.target.value)}
            className="w-full bg-transparent text-xl font-semibold text-zinc-100 border-b border-transparent hover:border-zinc-700 focus:border-indigo-500 focus:outline-none pb-1 transition-colors"
            placeholder="Title"
          />

          <textarea
            value={summary}
            onChange={(e) => set('summary', e.target.value)}
            placeholder="Summary"
            rows={2}
            className="w-full bg-zinc-900 border border-zinc-800 rounded-lg p-3 text-sm text-zinc-300 placeholder:text-zinc-600 focus:outline-none focus:border-indigo-500 resize-none transition-colors"
          />

          <div>
            <label className="text-xs text-zinc-600 uppercase tracking-wider mb-1.5 block">
              Content
            </label>
            <textarea
              value={content}
              onChange={(e) => set('content', e.target.value)}
              placeholder="Full content…"
              rows={14}
              className="w-full bg-zinc-900 border border-zinc-800 rounded-lg p-3 text-sm text-zinc-300 placeholder:text-zinc-600 focus:outline-none focus:border-indigo-500 resize-none font-mono transition-colors leading-relaxed"
            />
          </div>

          <div>
            <label className="text-xs text-zinc-600 uppercase tracking-wider mb-1.5 block">
              Tags
            </label>
            <input
              value={
                tagsInput !== null
                  ? tagsInput
                  : (edits.tags ?? memory.tags).join(', ')
              }
              onChange={(e) => {
                setTagsInput(e.target.value)
                setHasChanges(true)
              }}
              placeholder="react, typescript, debugging"
              className="w-full bg-zinc-900 border border-zinc-800 rounded-lg p-3 text-sm text-zinc-300 placeholder:text-zinc-600 focus:outline-none focus:border-indigo-500 transition-colors"
            />
            {displayedTags.length > 0 && (
              <div className="flex flex-wrap gap-1.5 mt-2">
                {displayedTags.map((tag) => (
                  <span
                    key={tag}
                    className="px-2 py-0.5 bg-zinc-800 text-zinc-400 rounded text-xs"
                  >
                    {tag}
                  </span>
                ))}
              </div>
            )}
          </div>
        </div>

        {/* Sidebar */}
        <div className="space-y-4">
          {/* Strength */}
          <div className="bg-zinc-900 border border-zinc-800 rounded-xl p-4">
            <div className="flex items-center justify-between mb-3">
              <span className="text-xs text-zinc-500 uppercase tracking-wider">
                Strength
              </span>
              <span className={`text-lg font-bold tabular-nums ${strengthColor(strength)}`}>
                {strength.toFixed(3)}
              </span>
            </div>
            <div className="h-2.5 bg-zinc-800 rounded-full overflow-hidden">
              <div
                className={`h-full rounded-full transition-all duration-500 ${strengthBg(strength)}`}
                style={{ width: `${Math.round(strength * 100)}%` }}
              />
            </div>
            <div className="flex justify-between text-xs text-zinc-700 mt-1">
              <span>0</span>
              <span>1.0</span>
            </div>
          </div>

          {/* Metadata */}
          <div className="bg-zinc-900 border border-zinc-800 rounded-xl p-4">
            <span className="text-xs text-zinc-500 uppercase tracking-wider block mb-3">
              Metadata
            </span>
            <div className="space-y-2">
              {metaRows.map(({ label, value }) => (
                <div key={label} className="flex justify-between gap-2 text-xs">
                  <span className="text-zinc-600">{label}</span>
                  <span className="text-zinc-300 font-mono text-right">{value}</span>
                </div>
              ))}
            </div>
          </div>

          {/* Linked memories */}
          {memory.linkedNodeIds.length > 0 && (
            <div className="bg-zinc-900 border border-zinc-800 rounded-xl p-4">
              <span className="text-xs text-zinc-500 uppercase tracking-wider block mb-3">
                Linked ({memory.linkedNodeIds.length})
              </span>
              <div className="space-y-1.5">
                {memory.linkedNodeIds.slice(0, 5).map((linkedId) => (
                  <Link
                    key={linkedId}
                    to={`/memories/${linkedId}`}
                    className="flex items-center gap-1.5 text-xs text-indigo-400 hover:text-indigo-300 transition-colors"
                  >
                    <ExternalLink className="w-3 h-3 flex-shrink-0" />
                    <span className="font-mono truncate">{linkedId.slice(0, 8)}…</span>
                  </Link>
                ))}
              </div>
            </div>
          )}

          {/* Superseded info */}
          {memory.supersededBy && (
            <div className="bg-zinc-900 border border-yellow-900/40 rounded-xl p-4">
              <span className="text-xs text-yellow-600 uppercase tracking-wider block mb-2">
                Superseded by
              </span>
              <Link
                to={`/memories/${memory.supersededBy}`}
                className="text-xs text-yellow-400 hover:text-yellow-300 font-mono transition-colors"
              >
                {memory.supersededBy.slice(0, 8)}…
              </Link>
            </div>
          )}

          {/* ID */}
          <div className="px-3 py-2 bg-zinc-900 border border-zinc-800 rounded-lg">
            <div className="text-xs text-zinc-600 mb-1">ID</div>
            <div className="text-xs text-zinc-500 font-mono break-all">{memory.id}</div>
          </div>
        </div>
      </div>
    </div>
  )
}
