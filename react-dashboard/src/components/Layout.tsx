import { Outlet, NavLink } from 'react-router-dom'
import { useQuery } from '@tanstack/react-query'
import { Brain, LayoutDashboard, Database, MessageSquare, FileCode } from 'lucide-react'
import { api } from '../api'

const navItems = [
  { to: '/', label: 'Overview', icon: LayoutDashboard, end: true },
  { to: '/memories', label: 'Memories', icon: Database, end: false },
  { to: '/chat', label: 'Chat', icon: MessageSquare, end: false },
  { to: '/file-summary', label: 'File Summary', icon: FileCode, end: false },
]

export default function Layout() {
  const { data: health, isError } = useQuery({
    queryKey: ['health'],
    queryFn: api.health,
    refetchInterval: 30_000,
    retry: false,
  })

  const isOnline = !isError && health?.status === 'healthy'

  return (
    <div className="flex h-screen bg-zinc-950 text-zinc-100 overflow-hidden">
      <aside className="w-56 flex-shrink-0 flex flex-col bg-zinc-900 border-r border-zinc-800">
        {/* Logo */}
        <div className="flex items-center gap-3 px-4 py-5 border-b border-zinc-800">
          <div className="w-8 h-8 bg-indigo-500 rounded-lg flex items-center justify-center flex-shrink-0">
            <Brain className="w-4 h-4 text-white" />
          </div>
          <div className="min-w-0">
            <div className="text-sm font-semibold text-zinc-100 leading-tight">Agentic</div>
            <div className="text-xs text-zinc-500 leading-tight">Memory</div>
          </div>
        </div>

        {/* Nav */}
        <nav className="flex-1 p-3 space-y-0.5">
          {navItems.map(({ to, label, icon: Icon, end }) => (
            <NavLink
              key={to}
              to={to}
              end={end}
              className={({ isActive }) =>
                `flex items-center gap-3 px-3 py-2 rounded-lg text-sm transition-colors ${
                  isActive
                    ? 'bg-indigo-500/15 text-indigo-400 font-medium'
                    : 'text-zinc-400 hover:text-zinc-200 hover:bg-zinc-800'
                }`
              }
            >
              <Icon className="w-4 h-4 flex-shrink-0" />
              {label}
            </NavLink>
          ))}
        </nav>

        {/* Status */}
        <div className="p-4 border-t border-zinc-800 space-y-2">
          <div className="flex items-center gap-2">
            <div
              className={`w-2 h-2 rounded-full flex-shrink-0 ${
                isOnline ? 'bg-green-400' : 'bg-red-400'
              }`}
            />
            <span className="text-xs text-zinc-400">
              {isOnline ? 'Connected' : 'Disconnected'}
            </span>
          </div>
          <div className="text-xs text-zinc-600 font-mono">localhost:3377</div>
        </div>
      </aside>

      <main className="flex-1 overflow-y-auto min-w-0">
        <Outlet />
      </main>
    </div>
  )
}
