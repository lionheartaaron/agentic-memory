import { useState, useEffect, useCallback } from 'react'
import {
  Folder, File, FolderOpen, ChevronRight, X, HardDrive, FolderCheck, Loader2,
} from 'lucide-react'
import { api } from '../api'
import type { FsItem, BrowseResponse } from '../types'

export function fileColor(ext: string | null): string {
  switch (ext) {
    case '.ts': case '.tsx': return 'text-blue-400'
    case '.cs':              return 'text-purple-400'
    case '.py':              return 'text-yellow-400'
    case '.js': case '.jsx':
    case '.mjs': case '.cjs': return 'text-yellow-300'
    case '.json':            return 'text-orange-400'
    case '.go':              return 'text-cyan-400'
    case '.rs':              return 'text-orange-500'
    case '.md':              return 'text-zinc-400'
    default:                 return 'text-zinc-500'
  }
}

export function parseBreadcrumb(path: string): Array<{ label: string; fullPath: string }> {
  const isWindows = /^[A-Za-z]:/.test(path)
  const sep = isWindows ? '\\' : '/'
  const parts = path.replace(/\\/g, '/').split('/').filter(Boolean)
  return parts.map((part, i) => {
    let fullPath: string
    if (isWindows) {
      fullPath = i === 0 ? part + sep : parts.slice(0, i + 1).join(sep)
    } else {
      fullPath = '/' + parts.slice(0, i + 1).join('/')
    }
    return { label: part, fullPath }
  })
}

export function FileBrowser({
  initialPath,
  onSelect,
  onSelectDirectory,
  onNavigate,
  onClose,
}: {
  initialPath?: string
  onSelect?: (path: string) => void
  onSelectDirectory?: (path: string) => void
  onNavigate: (path: string) => void
  onClose: () => void
}) {
  const [data, setData] = useState<BrowseResponse | null>(null)
  const [loading, setLoading] = useState(false)
  const [error, setError] = useState<string | null>(null)

  const navigate = useCallback(async (path?: string) => {
    setLoading(true)
    setError(null)
    try {
      const res = await api.browsePath(path)
      setData(res)
      onNavigate(res.path)
    } catch (e) {
      setError(e instanceof Error ? e.message : 'Failed to browse directory')
    } finally {
      setLoading(false)
    }
  }, [onNavigate])

  useEffect(() => { navigate(initialPath) }, [navigate]) // eslint-disable-line react-hooks/exhaustive-deps

  const crumbs = data ? parseBreadcrumb(data.path) : []

  return (
    <div
      className="fixed inset-0 bg-black/60 flex items-center justify-center z-50"
      onClick={onClose}
    >
      <div
        className="bg-zinc-900 border border-zinc-700 rounded-xl w-[640px] flex flex-col shadow-2xl"
        style={{ maxHeight: '520px' }}
        onClick={e => e.stopPropagation()}
      >
        {/* Header */}
        <div className="flex items-center justify-between px-4 py-3 border-b border-zinc-800 flex-shrink-0">
          <span className="text-sm font-medium text-zinc-200">Browse Files</span>
          <div className="flex items-center gap-2">
            {onSelectDirectory && data && (
              <button
                onClick={() => onSelectDirectory(data.path)}
                className="flex items-center gap-1.5 px-3 py-1 bg-indigo-600 hover:bg-indigo-500 text-white text-xs font-medium rounded-lg transition-colors"
              >
                <FolderCheck className="w-3.5 h-3.5" />
                Select this folder
              </button>
            )}
            <button onClick={onClose} className="text-zinc-500 hover:text-zinc-200 transition-colors">
              <X className="w-4 h-4" />
            </button>
          </div>
        </div>

        {/* Breadcrumb */}
        <div className="flex items-center gap-1 px-4 py-2 border-b border-zinc-800 overflow-x-auto flex-shrink-0 min-h-[36px]">
          {data ? (
            <>
              <button
                onClick={() => navigate(undefined)}
                title="Working directory"
                className="text-zinc-500 hover:text-zinc-200 transition-colors flex-shrink-0"
              >
                <HardDrive className="w-3.5 h-3.5" />
              </button>
              {crumbs.map((c, i) => (
                <span key={c.fullPath} className="flex items-center gap-1 flex-shrink-0">
                  <ChevronRight className="w-3 h-3 text-zinc-700" />
                  <button
                    onClick={() => navigate(c.fullPath)}
                    className={`text-xs transition-colors hover:text-zinc-200 ${
                      i === crumbs.length - 1 ? 'text-zinc-200 font-medium' : 'text-zinc-500'
                    }`}
                  >
                    {c.label}
                  </button>
                </span>
              ))}
            </>
          ) : (
            <span className="text-xs text-zinc-600">Loading…</span>
          )}
        </div>

        {/* Items list */}
        <div className="flex-1 overflow-y-auto min-h-0">
          {loading && (
            <div className="flex items-center justify-center py-12">
              <Loader2 className="w-5 h-5 animate-spin text-zinc-500" />
            </div>
          )}
          {error && !loading && (
            <div className="px-4 py-3 text-sm text-red-400">{error}</div>
          )}
          {!loading && data && (
            <div className="py-1">
              {data.parent !== null && (
                <button
                  onClick={() => navigate(data.parent!)}
                  className="w-full flex items-center gap-3 px-4 py-2 text-sm text-zinc-500 hover:bg-zinc-800 hover:text-zinc-300 transition-colors"
                >
                  <FolderOpen className="w-4 h-4 flex-shrink-0 text-zinc-600" />
                  <span className="font-mono">..</span>
                </button>
              )}
              {data.items.length === 0 && (
                <div className="px-4 py-8 text-sm text-zinc-600 text-center">Empty directory</div>
              )}
              {data.items.map((item: FsItem) => (
                <button
                  key={item.fullPath}
                  onClick={() => {
                    if (item.isDirectory) navigate(item.fullPath)
                    else if (onSelect) onSelect(item.fullPath)
                  }}
                  className="w-full flex items-center gap-3 px-4 py-2 text-sm hover:bg-zinc-800 transition-colors text-left"
                >
                  {item.isDirectory ? (
                    <Folder className="w-4 h-4 flex-shrink-0 text-amber-400" />
                  ) : (
                    <File className={`w-4 h-4 flex-shrink-0 ${fileColor(item.extension)}`} />
                  )}
                  <span className={item.isDirectory ? 'text-zinc-200' : fileColor(item.extension)}>
                    {item.name}
                  </span>
                  {!item.isDirectory && item.extension && (
                    <span className="ml-auto text-xs text-zinc-600 font-mono flex-shrink-0">
                      {item.extension}
                    </span>
                  )}
                </button>
              ))}
            </div>
          )}
        </div>
      </div>
    </div>
  )
}
