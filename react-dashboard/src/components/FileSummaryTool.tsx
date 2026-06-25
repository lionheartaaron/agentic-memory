import { useState, useEffect } from 'react'
import { FileSearch, FolderOpen, Loader2, Sparkles } from 'lucide-react'
import { api } from '../api'
import { FileBrowser } from './FileBrowser'

const LAST_BROWSE_KEY = 'fileBrowser.lastPath'

export function FileSummaryTool({ defaultPath }: { defaultPath?: string }) {
  const [filePath, setFilePath] = useState('')
  const [context, setContext] = useState<string | null>(null)
  const [contextError, setContextError] = useState<string | null>(null)
  const [contextLoading, setContextLoading] = useState(false)
  const [summary, setSummary] = useState<string | null>(null)
  const [summaryError, setSummaryError] = useState<string | null>(null)
  const [summaryLoading, setSummaryLoading] = useState(false)
  const [browserOpen, setBrowserOpen] = useState(false)
  const [lastBrowsedPath, setLastBrowsedPath] = useState<string | undefined>(defaultPath)

  useEffect(() => {
    if (defaultPath) {
      setLastBrowsedPath(defaultPath)
      return
    }
    api.kv.get(LAST_BROWSE_KEY)
      .then(res => { if (res.value) setLastBrowsedPath(res.value) })
      .catch(() => {})
  }, [defaultPath])

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

  function handleFileSelected(path: string) {
    setFilePath(path)
    setBrowserOpen(false)
    setContext(null)
    setContextError(null)
    setSummary(null)
    setSummaryError(null)
  }

  function handleNavigate(path: string) {
    setLastBrowsedPath(path)
    if (!defaultPath) api.kv.set(LAST_BROWSE_KEY, path).catch(() => {})
  }

  return (
    <>
      {browserOpen && (
        <FileBrowser
          initialPath={lastBrowsedPath ?? (filePath.trim() || undefined)}
          onSelect={handleFileSelected}
          onNavigate={handleNavigate}
          onClose={() => setBrowserOpen(false)}
        />
      )}

      <div className="space-y-6">
        <div className="space-y-2">
          <label className="block text-sm font-medium text-zinc-300">File Path</label>
          <div className="flex gap-3">
            <div className="flex-1 relative">
              <input
                type="text"
                value={filePath}
                onChange={e => setFilePath(e.target.value)}
                onKeyDown={e => e.key === 'Enter' && handleExtract()}
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

        {(context !== null || contextError) && (
          <div className="space-y-3">
            <div className="flex items-center justify-between">
              <h3 className="text-sm font-medium text-zinc-300">Extracted Context</h3>
              {context && (
                <span className="text-xs text-zinc-500">{context.split('\n').length} lines</span>
              )}
            </div>
            {contextError ? (
              <div className="rounded-lg border border-red-800/50 bg-red-950/30 px-4 py-3 text-sm text-red-400">
                {contextError}
              </div>
            ) : (
              <pre className="bg-zinc-900 border border-zinc-800 rounded-lg p-4 text-xs text-zinc-300 font-mono overflow-x-auto whitespace-pre-wrap leading-relaxed max-h-80 overflow-y-auto">
                {context}
              </pre>
            )}
          </div>
        )}

        {context && (
          <div className="space-y-4 border-t border-zinc-800 pt-5">
            <div className="flex items-center justify-between">
              <div>
                <h3 className="text-sm font-medium text-zinc-300 flex items-center gap-2">
                  <Sparkles className="w-4 h-4 text-indigo-400" />
                  AI Description
                </h3>
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
                  <><Loader2 className="w-4 h-4 animate-spin" />Generating…</>
                ) : (
                  <><Sparkles className="w-4 h-4" />Generate</>
                )}
              </button>
            </div>

            {summaryLoading && (
              <div className="flex items-center gap-3 rounded-lg border border-zinc-800 bg-zinc-900/50 px-4 py-5">
                <Loader2 className="w-5 h-5 animate-spin text-indigo-400 flex-shrink-0" />
                <span className="text-sm text-zinc-400">Running local inference — this may take a moment…</span>
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
