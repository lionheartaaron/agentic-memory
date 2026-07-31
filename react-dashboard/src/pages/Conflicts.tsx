import { useState } from 'react'
import { Link } from 'react-router-dom'
import { useQuery, useMutation, useQueryClient } from '@tanstack/react-query'
import { GitFork, Loader2, Check, X, ShieldAlert, Clock } from 'lucide-react'
import { api } from '../api'
import { timeAgo } from '../utils'
import {
  CONFLICT_KIND_LABELS, CONFLICT_KIND_HELP, CONFLICT_STATUS_LABELS,
  STATE_LABELS, SOURCE_LABELS, label,
} from '../types'
import type { ConflictDetail, ConflictSide } from '../types'

/**
 * One side of a contradiction.
 *
 * Comes down with the conflict rather than being fetched by id, which is what lets a forgotten
 * side be shown at all: those are deliberately unfetchable, and a blank half of a decision is
 * worse than no decision. Null means the current scope may not see it.
 */
function Side({
  side, role, onChoose, choosing, disabled, settled,
}: {
  side: ConflictSide | null
  role: 'Existing' | 'New'
  onChoose: () => void
  choosing: boolean
  disabled: boolean
  settled: boolean
}) {
  if (!side) {
    return (
      <div className="flex flex-col bg-zinc-950/60 border border-zinc-800 rounded-lg p-3 min-w-0">
        <span className="text-[10px] uppercase tracking-wider text-zinc-500 mb-2">{role}</span>
        <p className="text-xs text-zinc-600 italic py-2">Not visible in this scope.</p>
      </div>
    )
  }

  const current = side.state === 0

  return (
    <div className="flex flex-col bg-zinc-950/60 border border-zinc-800 rounded-lg p-3 min-w-0">
      <div className="flex items-center justify-between gap-2 mb-2">
        <span className="text-[10px] uppercase tracking-wider text-zinc-500">{role}</span>
        <Link
          to={`/memories/${side.id}`}
          className="text-[10px] text-indigo-400 hover:text-indigo-300 font-mono"
        >
          {side.id.slice(0, 8)}…
        </Link>
      </div>

      <p className="text-sm text-zinc-200 leading-snug">{side.title}</p>

      {side.summary && (
        <p className="text-xs text-zinc-500 mt-1 line-clamp-2 leading-relaxed">{side.summary}</p>
      )}

      {side.valueKey && (
        <p className="text-xs text-zinc-400 mt-1.5 font-mono">{side.valueKey}</p>
      )}

      <div className="flex items-center gap-2 flex-wrap mt-2 text-[10px] text-zinc-600">
        {!current && (
          <span className="px-1.5 py-0.5 rounded bg-zinc-800 text-zinc-400">
            {label(STATE_LABELS, side.state)}
          </span>
        )}
        <span className="flex items-center gap-1">
          <Clock className="w-3 h-3" /> {timeAgo(side.validFrom)}
        </span>
        <span>{label(SOURCE_LABELS, side.source)}</span>
      </div>

      {!settled && (
        <button
          onClick={onChoose}
          disabled={disabled}
          className="mt-3 flex items-center justify-center gap-1.5 px-2 py-1.5 text-xs font-medium rounded-lg border border-zinc-700 text-zinc-300 hover:bg-zinc-800 hover:border-zinc-600 transition-colors disabled:opacity-40"
        >
          {choosing ? <Loader2 className="w-3 h-3 animate-spin" /> : <Check className="w-3 h-3" />}
          Keep this one
        </button>
      )}
    </div>
  )
}

function ConflictCard({ detail }: { detail: ConflictDetail }) {
  const conflict = detail.conflict
  const queryClient = useQueryClient()
  const [choice, setChoice] = useState<string | null>(null)

  const resolve = useMutation({
    mutationFn: ({ winnerId, dismiss }: { winnerId?: string; dismiss?: boolean }) =>
      api.resolveConflict(conflict.id, winnerId, dismiss ?? false),
    onSuccess: () => {
      queryClient.invalidateQueries({ queryKey: ['conflicts'] })
      queryClient.invalidateQueries({ queryKey: ['memories'] })
      setChoice(null)
    },
  })

  const settled = conflict.status !== 0
  const immutable = conflict.kind === 2 || conflict.kind === 3

  return (
    <div
      className={`bg-zinc-900 border rounded-xl p-4 space-y-3 ${
        settled ? 'border-zinc-800/60 opacity-70' : 'border-amber-900/40'
      }`}
    >
      <div className="flex items-start justify-between gap-3">
        <div className="min-w-0">
          <div className="flex items-center gap-2 flex-wrap">
            <span
              className={`px-2 py-0.5 rounded text-[10px] font-medium ${
                immutable
                  ? 'bg-red-500/10 text-red-400'
                  : 'bg-amber-500/10 text-amber-400'
              }`}
            >
              {label(CONFLICT_KIND_LABELS, conflict.kind)}
            </span>

            {conflict.predicate && (
              <span className="text-[10px] text-zinc-500 font-mono">
                {conflict.subjectRef}.{conflict.predicate}
              </span>
            )}

            {settled && (
              <span className="text-[10px] text-zinc-500">
                {label(CONFLICT_STATUS_LABELS, conflict.status)}
                {conflict.resolvedAt && ` · ${timeAgo(conflict.resolvedAt)}`}
              </span>
            )}
          </div>

          <p className="text-sm text-zinc-300 mt-2 leading-relaxed">{conflict.description}</p>
          <p className="text-xs text-zinc-600 mt-1">
            {CONFLICT_KIND_HELP[conflict.kind] ?? ''}
          </p>
        </div>

        <span className="text-[10px] text-zinc-600 flex-shrink-0 whitespace-nowrap">
          {timeAgo(conflict.detectedAt)}
        </span>
      </div>

      {immutable && !settled && (
        <div className="flex items-start gap-2 px-3 py-2 bg-red-950/20 border border-red-900/40 rounded-lg">
          <ShieldAlert className="w-3.5 h-3.5 text-red-400 flex-shrink-0 mt-0.5" />
          <p className="text-xs text-red-300/90">
            {conflict.kind === 3
              ? 'One side is shared by every companion. Choosing the scoped one removes that knowledge from all the others.'
              : 'This contradicts something declared immutable. Confirm with the user before choosing.'}
          </p>
        </div>
      )}

      <div className="grid grid-cols-1 sm:grid-cols-2 gap-3">
        <Side
          side={detail.existing}
          role="Existing"
          settled={settled}
          choosing={resolve.isPending && choice === conflict.existingMemoryId}
          disabled={resolve.isPending}
          onChoose={() => {
            setChoice(conflict.existingMemoryId)
            resolve.mutate({ winnerId: conflict.existingMemoryId })
          }}
        />
        <Side
          side={detail.new}
          role="New"
          settled={settled}
          choosing={resolve.isPending && choice === conflict.newMemoryId}
          disabled={resolve.isPending}
          onChoose={() => {
            setChoice(conflict.newMemoryId)
            resolve.mutate({ winnerId: conflict.newMemoryId })
          }}
        />
      </div>

      {!settled && (
        <div className="flex items-center justify-between gap-3 pt-1">
          <p className="text-[10px] text-zinc-600">
            The loser becomes history. Nothing here is deleted.
          </p>
          <button
            onClick={() => {
              setChoice('dismiss')
              resolve.mutate({ dismiss: true })
            }}
            disabled={resolve.isPending}
            className="flex items-center gap-1.5 px-2.5 py-1 text-xs text-zinc-500 hover:text-zinc-300 transition-colors disabled:opacity-40"
          >
            <X className="w-3 h-3" />
            Not a contradiction
          </button>
        </div>
      )}

      {resolve.isError && (
        <p className="text-xs text-red-400">{(resolve.error as Error).message}</p>
      )}
    </div>
  )
}

export default function Conflicts() {
  const [showSettled, setShowSettled] = useState(false)

  const { data: conflicts, isLoading } = useQuery({
    queryKey: ['conflicts', showSettled],
    queryFn: () => api.conflicts(!showSettled),
    refetchInterval: 20_000,
  })

  const open = conflicts?.filter((d) => d.conflict.status === 0) ?? []

  return (
    <div className="p-6 space-y-5 max-w-4xl mx-auto">
      <div className="flex items-center justify-between gap-4">
        <div className="flex items-center gap-3">
          <div className="w-10 h-10 rounded-xl bg-zinc-800 border border-zinc-700 flex items-center justify-center flex-shrink-0">
            <GitFork className="w-5 h-5 text-zinc-300" />
          </div>
          <div>
            <h1 className="text-xl font-semibold text-zinc-100">Conflicts</h1>
            <p className="text-xs text-zinc-500 mt-0.5">
              {isLoading ? 'Loading…' : `${open.length} awaiting a decision`}
            </p>
          </div>
        </div>

        <label className="flex items-center gap-2 text-xs text-zinc-500 cursor-pointer">
          <input
            type="checkbox"
            checked={showSettled}
            onChange={(e) => setShowSettled(e.target.checked)}
            className="accent-indigo-500"
          />
          Include settled
        </label>
      </div>

      <p className="text-xs text-zinc-600 leading-relaxed">
        These are contradictions the store found and deliberately did not resolve on its own. A
        similarity score can tell that two memories are about the same thing; it cannot tell whether
        the user changed their mind, misspoke, or was talking about someone else. That is the
        judgement being asked for here.
      </p>

      {isLoading ? (
        <div className="flex items-center gap-2 text-zinc-600 text-sm">
          <Loader2 className="w-4 h-4 animate-spin" /> Loading…
        </div>
      ) : conflicts && conflicts.length > 0 ? (
        <div className="space-y-3">
          {conflicts.map((d) => (
            <ConflictCard key={d.conflict.id} detail={d} />
          ))}
        </div>
      ) : (
        <div className="py-20 text-center">
          <p className="text-zinc-600 text-sm">
            {showSettled ? 'No conflicts recorded.' : 'Nothing waiting on a decision.'}
          </p>
        </div>
      )}
    </div>
  )
}
