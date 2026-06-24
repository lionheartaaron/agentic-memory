import { useState, useEffect, useRef } from 'react'
import { Link } from 'react-router-dom'
import { useQuery } from '@tanstack/react-query'
import { Search, Plus, X, Tag, Pin, Clock, Zap } from 'lucide-react'
import { api } from '../api'
import { getCurrentStrength, strengthColor, strengthBg, timeAgo } from '../utils'
import type { Memory } from '../types'
import CreateMemoryModal from '../components/CreateMemoryModal'

function useDebounce<T>(value: T, delay: number): T {
  const [debounced, setDebounced] = useState(value)
  useEffect(() => {
    const timer = setTimeout(() => setDebounced(value), delay)
    return () => clearTimeout(timer)
  }, [value, delay])
  return debounced
}

function MemoryCard({ memory, score }: { memory: Memory; score?: number }) {
  const strength = getCurrentStrength(memory)
  return (
    <Link
      to={`/memories/${memory.id}`}
      className="flex flex-col bg-zinc-900 border border-zinc-800 rounded-xl p-4 hover:border-zinc-600 hover:bg-zinc-800/30 transition-all group"
    >
      <div className="flex items-start gap-2 mb-2">
        {memory.isPinned && (
          <Pin className="w-3 h-3 text-indigo-400 mt-0.5 flex-shrink-0" />
        )}
        <h3 className="text-sm font-medium text-zinc-200 group-hover:text-indigo-400 transition-colors line-clamp-2 flex-1 leading-snug">
          {memory.title}
        </h3>
        {score !== undefined && (
          <span className="text-xs text-zinc-600 tabular-nums flex-shrink-0 pt-0.5">
            {Math.round(score * 100)}%
          </span>
        )}
      </div>

      {memory.summary && (
        <p className="text-xs text-zinc-500 line-clamp-2 mb-3 leading-relaxed flex-1">
          {memory.summary}
        </p>
      )}

      {memory.tags.length > 0 && (
        <div className="flex flex-wrap gap-1 mb-3">
          {memory.tags.slice(0, 4).map((tag) => (
            <span key={tag} className="px-1.5 py-0.5 bg-zinc-800 text-zinc-400 rounded text-xs">
              {tag}
            </span>
          ))}
          {memory.tags.length > 4 && (
            <span className="px-1.5 py-0.5 text-zinc-600 text-xs">
              +{memory.tags.length - 4}
            </span>
          )}
        </div>
      )}

      <div className="space-y-1.5 mt-auto">
        <div className="flex items-center gap-2">
          <div className="flex-1 h-1 bg-zinc-800 rounded-full overflow-hidden">
            <div
              className={`h-full rounded-full ${strengthBg(strength)}`}
              style={{ width: `${Math.round(strength * 100)}%` }}
            />
          </div>
          <span className={`text-xs tabular-nums ${strengthColor(strength)}`}>
            {strength.toFixed(2)}
          </span>
        </div>

        <div className="flex items-center justify-between text-xs text-zinc-600">
          <span className="flex items-center gap-1">
            <Clock className="w-3 h-3" />
            {timeAgo(memory.createdAt)}
          </span>
          {memory.accessCount > 0 && (
            <span className="flex items-center gap-1">
              <Zap className="w-3 h-3" />
              {memory.accessCount}×
            </span>
          )}
        </div>
      </div>
    </Link>
  )
}

function SkeletonCard() {
  return (
    <div className="bg-zinc-900 border border-zinc-800 rounded-xl p-4 animate-pulse space-y-3">
      <div className="h-4 bg-zinc-800 rounded w-4/5" />
      <div className="h-3 bg-zinc-800 rounded w-full" />
      <div className="h-3 bg-zinc-800 rounded w-3/4" />
      <div className="flex gap-1">
        <div className="h-4 w-12 bg-zinc-800 rounded" />
        <div className="h-4 w-16 bg-zinc-800 rounded" />
      </div>
      <div className="h-1 bg-zinc-800 rounded-full" />
    </div>
  )
}

export default function Browse() {
  const [query, setQuery] = useState('')
  const [selectedTags, setSelectedTags] = useState<string[]>([])
  const [showCreate, setShowCreate] = useState(false)
  const debouncedQuery = useDebounce(query, 300)
  const searchInputRef = useRef<HTMLInputElement>(null)

  const { data: allMemories, isLoading: listLoading } = useQuery({
    queryKey: ['memories'],
    queryFn: () => api.list(),
  })

  const { data: searchResults, isFetching: searching } = useQuery({
    queryKey: ['search', debouncedQuery, selectedTags],
    queryFn: () =>
      api.search(debouncedQuery, 50, selectedTags.length ? selectedTags : undefined),
    enabled: debouncedQuery.length > 1,
  })

  useEffect(() => {
    const handler = (e: KeyboardEvent) => {
      if ((e.ctrlKey || e.metaKey) && e.key === 'k') {
        e.preventDefault()
        searchInputRef.current?.focus()
      }
      if ((e.ctrlKey || e.metaKey) && e.key === 'n') {
        e.preventDefault()
        setShowCreate(true)
      }
      if (e.key === 'Escape') {
        if (document.activeElement === searchInputRef.current) {
          setQuery('')
          searchInputRef.current?.blur()
        } else {
          setShowCreate(false)
        }
      }
    }
    window.addEventListener('keydown', handler)
    return () => window.removeEventListener('keydown', handler)
  }, [])

  const allTags = [...new Set(allMemories?.flatMap((m) => m.tags) ?? [])].sort()

  const items: Array<{ memory: Memory; score?: number }> =
    debouncedQuery.length > 1
      ? (searchResults?.map((r) => ({ memory: r.memory, score: r.score })) ?? [])
      : (allMemories
          ?.filter(
            (m) =>
              !selectedTags.length || selectedTags.every((t) => m.tags.includes(t)),
          )
          .map((m) => ({ memory: m })) ?? [])

  const toggleTag = (tag: string) =>
    setSelectedTags((prev) =>
      prev.includes(tag) ? prev.filter((t) => t !== tag) : [...prev, tag],
    )

  const isLoading = listLoading && !allMemories

  return (
    <div className="p-6 space-y-5 max-w-7xl mx-auto">
      {/* Header */}
      <div className="flex items-center justify-between">
        <div>
          <h1 className="text-xl font-semibold text-zinc-100">Memories</h1>
          <p className="text-sm text-zinc-500 mt-0.5">
            {isLoading
              ? 'Loading…'
              : `${items.length} ${debouncedQuery.length > 1 ? 'results' : 'memories'}`}
          </p>
        </div>
        <button
          onClick={() => setShowCreate(true)}
          className="flex items-center gap-2 px-3 py-1.5 bg-indigo-500 hover:bg-indigo-600 text-white rounded-lg text-sm font-medium transition-colors"
          title="New memory (⌘N)"
        >
          <Plus className="w-4 h-4" />
          New Memory
        </button>
      </div>

      {/* Search */}
      <div className="space-y-3">
        <div className="relative">
          <Search className="absolute left-3 top-1/2 -translate-y-1/2 w-4 h-4 text-zinc-500 pointer-events-none" />
          <input
            ref={searchInputRef}
            value={query}
            onChange={(e) => setQuery(e.target.value)}
            placeholder="Semantic search… (⌘K)"
            className="w-full pl-10 pr-10 py-2.5 bg-zinc-900 border border-zinc-800 rounded-xl text-sm text-zinc-200 placeholder:text-zinc-600 focus:outline-none focus:border-indigo-500 transition-colors"
          />
          {query && !searching && (
            <button
              onClick={() => setQuery('')}
              className="absolute right-3 top-1/2 -translate-y-1/2 text-zinc-500 hover:text-zinc-300 transition-colors"
            >
              <X className="w-4 h-4" />
            </button>
          )}
          {searching && (
            <div className="absolute right-3 top-1/2 -translate-y-1/2 w-4 h-4 border-2 border-indigo-500 border-t-transparent rounded-full animate-spin" />
          )}
        </div>

        {allTags.length > 0 && (
          <div className="flex items-center gap-2 flex-wrap">
            <Tag className="w-3.5 h-3.5 text-zinc-700 flex-shrink-0" />
            {allTags.map((tag) => (
              <button
                key={tag}
                onClick={() => toggleTag(tag)}
                className={`px-2.5 py-1 rounded-full text-xs font-medium transition-colors ${
                  selectedTags.includes(tag)
                    ? 'bg-indigo-500 text-white'
                    : 'bg-zinc-800 text-zinc-400 hover:bg-zinc-700 hover:text-zinc-200'
                }`}
              >
                {tag}
              </button>
            ))}
            {selectedTags.length > 0 && (
              <button
                onClick={() => setSelectedTags([])}
                className="text-xs text-zinc-600 hover:text-zinc-400 transition-colors"
              >
                Clear filters
              </button>
            )}
          </div>
        )}
      </div>

      {/* Grid */}
      {isLoading ? (
        <div className="grid grid-cols-1 sm:grid-cols-2 lg:grid-cols-3 xl:grid-cols-4 gap-3">
          {Array.from({ length: 8 }).map((_, i) => (
            <SkeletonCard key={i} />
          ))}
        </div>
      ) : items.length > 0 ? (
        <div className="grid grid-cols-1 sm:grid-cols-2 lg:grid-cols-3 xl:grid-cols-4 gap-3">
          {items.map(({ memory, score }) => (
            <MemoryCard key={memory.id} memory={memory} score={score} />
          ))}
        </div>
      ) : (
        <div className="py-24 text-center">
          <p className="text-zinc-600 text-sm">
            {debouncedQuery.length > 1
              ? `No results for "${debouncedQuery}"`
              : 'No memories yet'}
          </p>
          {!debouncedQuery && (
            <button
              onClick={() => setShowCreate(true)}
              className="mt-3 text-indigo-400 hover:text-indigo-300 text-sm transition-colors"
            >
              Create your first memory →
            </button>
          )}
        </div>
      )}

      {showCreate && <CreateMemoryModal onClose={() => setShowCreate(false)} />}
    </div>
  )
}
