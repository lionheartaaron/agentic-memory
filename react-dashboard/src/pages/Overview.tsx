import { useQuery } from '@tanstack/react-query'
import { Link } from 'react-router-dom'
import { Database, Zap, AlertTriangle, HardDrive, ArrowRight } from 'lucide-react'
import type { ComponentType } from 'react'
import { api } from '../api'
import { formatBytes, timeAgo, getCurrentStrength, strengthColor, strengthBg } from '../utils'
import type { Memory } from '../types'

function StatCard({
  icon: Icon,
  label,
  value,
  sub,
  iconColor = 'text-indigo-400',
}: {
  icon: ComponentType<{ className?: string }>
  label: string
  value: string | number
  sub?: string
  iconColor?: string
}) {
  return (
    <div className="bg-zinc-900 border border-zinc-800 rounded-xl p-5">
      <div className="flex items-start justify-between mb-3">
        <span className="text-xs text-zinc-500 uppercase tracking-wider">{label}</span>
        <Icon className={`w-4 h-4 ${iconColor}`} />
      </div>
      <div className="text-2xl font-bold text-zinc-100 tabular-nums">{value}</div>
      {sub && <div className="text-xs text-zinc-600 mt-1">{sub}</div>}
    </div>
  )
}

function RecentRow({ memory }: { memory: Memory }) {
  const strength = getCurrentStrength(memory)
  return (
    <Link
      to={`/memories/${memory.id}`}
      className="flex items-center gap-4 px-4 py-3 hover:bg-zinc-800/40 transition-colors group"
    >
      <div className="flex-1 min-w-0">
        <div className="text-sm font-medium text-zinc-200 truncate group-hover:text-indigo-400 transition-colors">
          {memory.title}
        </div>
        {memory.summary && (
          <div className="text-xs text-zinc-500 truncate mt-0.5">{memory.summary}</div>
        )}
      </div>
      <div className="flex items-center gap-2 flex-shrink-0 w-32">
        <div className="flex-1 h-1 bg-zinc-800 rounded-full overflow-hidden">
          <div
            className={`h-full rounded-full ${strengthBg(strength)}`}
            style={{ width: `${Math.round(strength * 100)}%` }}
          />
        </div>
        <span className={`text-xs tabular-nums w-8 text-right ${strengthColor(strength)}`}>
          {strength.toFixed(2)}
        </span>
      </div>
      <div className="text-xs text-zinc-600 w-16 text-right flex-shrink-0">
        {timeAgo(memory.createdAt)}
      </div>
    </Link>
  )
}

function Skeleton() {
  return (
    <div className="px-4 py-3 animate-pulse">
      <div className="h-4 bg-zinc-800 rounded w-3/4 mb-1.5" />
      <div className="h-3 bg-zinc-800 rounded w-1/2" />
    </div>
  )
}

export default function Overview() {
  const { data: stats, isLoading: statsLoading } = useQuery({
    queryKey: ['stats'],
    queryFn: api.stats,
    refetchInterval: 30_000,
  })

  const { data: memories, isLoading: memoriesLoading } = useQuery({
    queryKey: ['memories'],
    queryFn: () => api.list(),
    refetchInterval: 30_000,
  })

  const recent = memories
    ?.slice()
    .sort((a, b) => new Date(b.createdAt).getTime() - new Date(a.createdAt).getTime())
    .slice(0, 10)

  return (
    <div className="p-6 space-y-6 max-w-5xl mx-auto">
      <div>
        <h1 className="text-xl font-semibold text-zinc-100">Overview</h1>
        <p className="text-sm text-zinc-500 mt-0.5">Memory store health and statistics</p>
      </div>

      {/* Stats */}
      <div className="grid grid-cols-2 lg:grid-cols-4 gap-3">
        <StatCard
          icon={Database}
          label="Total Memories"
          value={statsLoading ? '—' : (stats?.totalNodes ?? 0)}
          sub="active nodes"
          iconColor="text-indigo-400"
        />
        <StatCard
          icon={Zap}
          label="Avg Strength"
          value={statsLoading ? '—' : (stats?.averageStrength?.toFixed(3) ?? '—')}
          sub="across all nodes"
          iconColor="text-yellow-400"
        />
        <StatCard
          icon={AlertTriangle}
          label="Weak Memories"
          value={statsLoading ? '—' : (stats?.weakMemoriesCount ?? 0)}
          sub="below threshold"
          iconColor={
            stats?.weakMemoriesCount ? 'text-red-400' : 'text-zinc-700'
          }
        />
        <StatCard
          icon={HardDrive}
          label="Database Size"
          value={statsLoading ? '—' : formatBytes(stats?.databaseSizeBytes ?? 0)}
          sub="on disk"
          iconColor="text-zinc-500"
        />
      </div>

      {/* Strength distribution bar */}
      {memories && memories.length > 0 && (
        <div className="bg-zinc-900 border border-zinc-800 rounded-xl p-5">
          <h2 className="text-sm font-medium text-zinc-300 mb-4">Strength Distribution</h2>
          <div className="space-y-2">
            {[
              { label: 'Strong (≥ 0.7)', min: 0.7, max: 1.01, color: 'bg-green-400' },
              { label: 'Medium (0.4–0.7)', min: 0.4, max: 0.7, color: 'bg-yellow-400' },
              { label: 'Weak (< 0.4)', min: 0, max: 0.4, color: 'bg-red-400' },
            ].map(({ label, min, max, color }) => {
              const count = memories.filter((m) => {
                const s = getCurrentStrength(m)
                return s >= min && s < max
              }).length
              const pct = (count / memories.length) * 100
              return (
                <div key={label} className="flex items-center gap-3">
                  <div className="w-32 text-xs text-zinc-500 flex-shrink-0">{label}</div>
                  <div className="flex-1 h-2 bg-zinc-800 rounded-full overflow-hidden">
                    <div
                      className={`h-full rounded-full transition-all ${color}`}
                      style={{ width: `${pct}%` }}
                    />
                  </div>
                  <div className="text-xs text-zinc-500 w-8 text-right tabular-nums flex-shrink-0">
                    {count}
                  </div>
                </div>
              )
            })}
          </div>
        </div>
      )}

      {/* Recent memories */}
      <div className="bg-zinc-900 border border-zinc-800 rounded-xl overflow-hidden">
        <div className="flex items-center justify-between px-4 py-3 border-b border-zinc-800">
          <h2 className="text-sm font-medium text-zinc-300">Recent Memories</h2>
          <Link
            to="/memories"
            className="flex items-center gap-1 text-xs text-indigo-400 hover:text-indigo-300 transition-colors"
          >
            View all <ArrowRight className="w-3 h-3" />
          </Link>
        </div>
        {memoriesLoading ? (
          <div className="divide-y divide-zinc-800/50">
            {Array.from({ length: 5 }).map((_, i) => (
              <Skeleton key={i} />
            ))}
          </div>
        ) : recent?.length ? (
          <div className="divide-y divide-zinc-800/30">
            {recent.map((memory) => (
              <RecentRow key={memory.id} memory={memory} />
            ))}
          </div>
        ) : (
          <div className="py-16 text-center text-zinc-600 text-sm">
            No memories yet.{' '}
            <Link to="/memories" className="text-indigo-400 hover:text-indigo-300">
              Create your first memory →
            </Link>
          </div>
        )}
      </div>
    </div>
  )
}
