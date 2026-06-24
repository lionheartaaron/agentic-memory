import { useState, useEffect, useCallback } from 'react'
import {
  FileCode, Loader2, Sparkles, FileSearch,
  Folder, File, FolderOpen, ChevronRight, X, HardDrive,
} from 'lucide-react'
import { api } from '../api'
import type { FsItem, BrowseResponse } from '../types'

// ── File browser helpers ───────────────────────────────────────────────────

function fileColor(ext: string | null): string {
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

function parseBreadcrumb(path: string): Array<{ label: string; fullPath: string }> {
  const isWindows = /^[A-Za-z]:/.test(path)
  const sep = isWindows ? '\\' : '/'
  const parts = path.replace(/\\/g, '/').split('/').filter(Boolean)

  return parts.map((part, i) => {
    let fullPath: string
    if (isWindows) {
      // Drive root must keep trailing backslash: "C:\" not "C:"
      fullPath = i === 0 ? part + sep : parts.slice(0, i + 1).join(sep)
    } else {
      fullPath = '/' + parts.slice(0, i + 1).join('/')
    }
    return { label: part, fullPath }
  })
}

// ── File browser modal ─────────────────────────────────────────────────────

function FileBrowserModal({
  initialPath,
  onSelect,
  onNavigate,
  onClose,
}: {
  initialPath?: string
  onSelect: (path: string) => void
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

  // Open at the initial path on mount only
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
          <button onClick={onClose} className="text-zinc-500 hover:text-zinc-200 transition-colors">
            <X className="w-4 h-4" />
          </button>
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
                      i === crumbs.length - 1
                        ? 'text-zinc-200 font-medium'
                        : 'text-zinc-500'
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
              {/* Navigate up */}
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
                <div className="px-4 py-8 text-sm text-zinc-600 text-center">
                  Empty directory
                </div>
              )}

              {data.items.map((item: FsItem) => (
                <button
                  key={item.fullPath}
                  onClick={() =>
                    item.isDirectory ? navigate(item.fullPath) : onSelect(item.fullPath)
                  }
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

const LAST_BROWSE_KEY = 'fileBrowser.lastPath'

// ── Main page ──────────────────────────────────────────────────────────────

export default function FileSummary() {
  const [filePath, setFilePath] = useState('')
  const [context, setContext] = useState<string | null>(null)
  const [contextError, setContextError] = useState<string | null>(null)
  const [contextLoading, setContextLoading] = useState(false)

  const [summary, setSummary] = useState<string | null>(null)
  const [summaryError, setSummaryError] = useState<string | null>(null)
  const [summaryLoading, setSummaryLoading] = useState(false)

  const [browserOpen, setBrowserOpen] = useState(false)
  const [lastBrowsedPath, setLastBrowsedPath] = useState<string | undefined>(undefined)

  // Restore the last browsed folder from the server KV store on mount
  useEffect(() => {
    api.kv.get(LAST_BROWSE_KEY)
      .then(res => { if (res.value) setLastBrowsedPath(res.value) })
      .catch(() => { /* silently ignore — not critical */ })
  }, [])

  async function handleExtract() {
    if (!filePath.trim()) return
    setContextLoading(true)
    setContext(null)
    setContextError(null)
    setSummary(null)
    setSummaryError(null)
    try {
      const res = await api.fileContext(filePath.trim())
      setContext(res.context)
    } catch (e) {
      setContextError(e instanceof Error ? e.message : 'Failed to extract context.')
    } finally {
      setContextLoading(false)
    }
  }

  async function handleGenerateSummary() {
    if (!filePath.trim()) return
    setSummaryLoading(true)
    setSummary(null)
    setSummaryError(null)
    try {
      const res = await api.fileSummary(filePath.trim())
      setSummary(res.summary)
    } catch (e) {
      setSummaryError(e instanceof Error ? e.message : 'Failed to generate summary.')
    } finally {
      setSummaryLoading(false)
    }
  }

  function handleKeyDown(e: React.KeyboardEvent<HTMLInputElement>) {
    if (e.key === 'Enter') handleExtract()
  }

  function handleFileSelected(path: string) {
    setFilePath(path)
    setBrowserOpen(false)
    setContext(null)
    setContextError(null)
    setSummary(null)
    setSummaryError(null)
  }

  return (
    <>
      {browserOpen && (
        <FileBrowserModal
          initialPath={lastBrowsedPath ?? (filePath.trim() || undefined)}
          onSelect={handleFileSelected}
          onNavigate={path => setLastBrowsedPath(path)}
          onClose={() => setBrowserOpen(false)}
        />
      )}

      <div className="p-8 max-w-4xl mx-auto space-y-8">
        {/* Header */}
        <div>
          <div className="flex items-center gap-3 mb-1">
            <FileCode className="w-6 h-6 text-indigo-400" />
            <h1 className="text-2xl font-semibold text-zinc-100">File Summary</h1>
          </div>
          <p className="text-sm text-zinc-400">
            Extract a structural context from any source file, then generate an AI description.
          </p>
        </div>

        {/* Input */}
        <div className="space-y-3">
          <label className="block text-sm font-medium text-zinc-300">File Path</label>
          <div className="flex gap-3">
            <div className="flex-1 relative">
              <input
                type="text"
                value={filePath}
                onChange={e => setFilePath(e.target.value)}
                onKeyDown={handleKeyDown}
                placeholder="e.g. C:\path\to\file.cs or /home/user/project/main.py"
                className="w-full bg-zinc-800 border border-zinc-700 rounded-lg pl-4 pr-10 py-2.5 text-sm text-zinc-100 placeholder-zinc-500 focus:outline-none focus:ring-2 focus:ring-indigo-500 focus:border-transparent font-mono"
              />
              <button
                onClick={() => setBrowserOpen(true)}
                title="Browse files"
                className="absolute right-2 top-1/2 -translate-y-1/2 p-1.5 rounded text-zinc-500 hover:text-zinc-200 hover:bg-zinc-700 transition-colors"
              >
                <FolderOpen className="w-4 h-4" />
              </button>
            </div>
            <button
              onClick={handleExtract}
              disabled={!filePath.trim() || contextLoading}
              className="flex items-center gap-2 px-4 py-2.5 bg-indigo-600 hover:bg-indigo-500 disabled:opacity-40 disabled:cursor-not-allowed text-white text-sm font-medium rounded-lg transition-colors"
            >
              {contextLoading ? (
                <Loader2 className="w-4 h-4 animate-spin" />
              ) : (
                <FileSearch className="w-4 h-4" />
              )}
              Extract Context
            </button>
          </div>
        </div>

        {/* Context output */}
        {(context !== null || contextError) && (
          <div className="space-y-3">
            <div className="flex items-center justify-between">
              <h2 className="text-sm font-medium text-zinc-300">Extracted Context</h2>
              {context && (
                <span className="text-xs text-zinc-500">
                  {context.split('\n').length} lines
                </span>
              )}
            </div>

            {contextError ? (
              <div className="rounded-lg border border-red-800/50 bg-red-950/30 px-4 py-3 text-sm text-red-400">
                {contextError}
              </div>
            ) : (
              <pre className="bg-zinc-900 border border-zinc-800 rounded-lg p-4 text-xs text-zinc-300 font-mono overflow-x-auto whitespace-pre-wrap leading-relaxed max-h-96 overflow-y-auto">
                {context}
              </pre>
            )}
          </div>
        )}

        {/* AI Summary — only shown once context is available */}
        {context && (
          <div className="space-y-4 border-t border-zinc-800 pt-6">
            <div className="flex items-center justify-between">
              <div>
                <h2 className="text-sm font-medium text-zinc-300 flex items-center gap-2">
                  <Sparkles className="w-4 h-4 text-indigo-400" />
                  AI Description
                </h2>
                <p className="text-xs text-zinc-500 mt-0.5">
                  Keyword-dense, 1–2 sentence description optimized for semantic search embedding (&lt;60 words).
                </p>
              </div>
              <button
                onClick={handleGenerateSummary}
                disabled={summaryLoading}
                className="flex items-center gap-2 px-4 py-2 bg-zinc-700 hover:bg-zinc-600 disabled:opacity-40 disabled:cursor-not-allowed text-white text-sm font-medium rounded-lg transition-colors"
              >
                {summaryLoading ? (
                  <>
                    <Loader2 className="w-4 h-4 animate-spin" />
                    Generating…
                  </>
                ) : (
                  <>
                    <Sparkles className="w-4 h-4" />
                    Generate
                  </>
                )}
              </button>
            </div>

            {summaryLoading && (
              <div className="flex items-center gap-3 rounded-lg border border-zinc-800 bg-zinc-900/50 px-4 py-5">
                <Loader2 className="w-5 h-5 animate-spin text-indigo-400 flex-shrink-0" />
                <span className="text-sm text-zinc-400">
                  Running local inference — this may take a moment…
                </span>
              </div>
            )}

            {summaryError && !summaryLoading && (
              <div className="rounded-lg border border-red-800/50 bg-red-950/30 px-4 py-3 text-sm text-red-400">
                {summaryError}
              </div>
            )}

            {summary && !summaryLoading && (
              <div className="rounded-lg border border-indigo-800/40 bg-indigo-950/20 px-5 py-4">
                <p className="text-sm text-zinc-200 leading-relaxed">{summary}</p>
              </div>
            )}
          </div>
        )}
      </div>
    </>
  )
}
