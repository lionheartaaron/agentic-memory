import { useState } from 'react'
import { KeyRound, Loader2, AlertTriangle } from 'lucide-react'
import { api, setApiKey, UnauthorizedError } from '../api'

/**
 * Shown when the server rejects a request for want of a key.
 *
 * The dashboard's own files are served without authentication on purpose, because a browser cannot
 * attach a header to its own page load. So the page always loads and it is the first `/api` call
 * that fails, which makes this overlay the only place a key can be asked for. Without it, setting
 * `Server:ApiKey` locks the dashboard out of its own server with no way back except editing
 * localStorage by hand.
 */
export default function ApiKeyGate({ onUnlocked }: { onUnlocked: () => void }) {
  const [key, setKey] = useState('')
  const [checking, setChecking] = useState(false)
  const [error, setError] = useState<string | null>(null)

  async function submit(event: React.FormEvent) {
    event.preventDefault()
    if (!key.trim() || checking) return

    setChecking(true)
    setError(null)
    setApiKey(key.trim())

    try {
      // A protected endpoint, so a wrong key is rejected here rather than silently accepted and
      // then failing on whichever page the user opens next.
      await api.stats()
      onUnlocked()
    } catch (err) {
      setApiKey('')
      setError(
        err instanceof UnauthorizedError
          ? 'That key was rejected.'
          : 'Could not reach the server.',
      )
    } finally {
      setChecking(false)
    }
  }

  return (
    <div className="fixed inset-0 z-50 flex items-center justify-center bg-zinc-950/90 backdrop-blur-sm p-6">
      <form
        onSubmit={submit}
        className="w-full max-w-md bg-zinc-900 border border-zinc-800 rounded-2xl p-6 space-y-5"
      >
        <div className="flex items-start gap-3">
          <div className="w-10 h-10 rounded-xl bg-amber-500/10 border border-amber-800/40 flex items-center justify-center flex-shrink-0">
            <KeyRound className="w-5 h-5 text-amber-400" />
          </div>
          <div>
            <h1 className="text-lg font-semibold text-zinc-100">API key required</h1>
            <p className="text-xs text-zinc-500 mt-1 leading-relaxed">
              This server has <code className="text-zinc-400">Server:ApiKey</code> set. Enter it to
              use the dashboard. It is kept in this browser only and sent as a header on every
              request.
            </p>
          </div>
        </div>

        <div className="space-y-2">
          <input
            type="password"
            value={key}
            onChange={(e) => setKey(e.target.value)}
            placeholder="API key"
            autoFocus
            autoComplete="off"
            className="w-full px-3 py-2 bg-zinc-950 border border-zinc-800 rounded-lg text-sm text-zinc-200 placeholder-zinc-600 focus:outline-none focus:border-zinc-600"
          />

          {error && (
            <p className="flex items-center gap-1.5 text-xs text-red-400">
              <AlertTriangle className="w-3.5 h-3.5 flex-shrink-0" />
              {error}
            </p>
          )}
        </div>

        <button
          type="submit"
          disabled={!key.trim() || checking}
          className="w-full flex items-center justify-center gap-2 px-3 py-2 text-sm font-medium rounded-lg bg-zinc-100 text-zinc-900 hover:bg-white transition-colors disabled:opacity-40 disabled:hover:bg-zinc-100"
        >
          {checking && <Loader2 className="w-3.5 h-3.5 animate-spin" />}
          Unlock
        </button>
      </form>
    </div>
  )
}
