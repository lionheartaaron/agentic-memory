import { useState } from 'react'
import { BrowserRouter, Routes, Route, Navigate } from 'react-router-dom'
import { QueryClient, QueryClientProvider, QueryCache, MutationCache } from '@tanstack/react-query'
import Layout from './components/Layout'
import ApiKeyGate from './components/ApiKeyGate'
import { UnauthorizedError } from './api'
import Overview from './pages/Overview'
import Browse from './pages/Browse'
import MemoryDetail from './pages/MemoryDetail'
import Conflicts from './pages/Conflicts'
import Chat from './pages/Chat'
import Projects from './pages/Projects'
import ProjectDetail from './pages/ProjectDetail'
import WorkerStatus from './pages/WorkerStatus'
import Settings from './pages/Settings'

export default function App() {
  const [locked, setLocked] = useState(false)

  // Built in state rather than at module scope so the caches below can reach setLocked.
  const [queryClient] = useState(() => {
    // Any query or mutation, anywhere, that comes back 401. Handling it in one place is what keeps
    // every page from having to know that authentication exists at all.
    const onError = (error: unknown) => {
      if (error instanceof UnauthorizedError) setLocked(true)
    }

    return new QueryClient({
      queryCache: new QueryCache({ onError }),
      mutationCache: new MutationCache({ onError }),
      defaultOptions: {
        queries: {
          staleTime: 10_000,
          // A rejected key is not a transient failure. Retrying only delays the prompt.
          retry: (failureCount, error) =>
            !(error instanceof UnauthorizedError) && failureCount < 1,
        },
      },
    })
  })

  return (
    <QueryClientProvider client={queryClient}>
      {locked && (
        <ApiKeyGate
          onUnlocked={() => {
            setLocked(false)
            queryClient.resetQueries()
          }}
        />
      )}

      <BrowserRouter>
        <Routes>
          <Route path="/" element={<Layout />}>
            <Route index element={<Overview />} />
            <Route path="memories" element={<Browse />} />
            <Route path="memories/:id" element={<MemoryDetail />} />
            <Route path="conflicts" element={<Conflicts />} />
            <Route path="chat" element={<Chat />} />
            <Route path="projects" element={<Projects />} />
            <Route path="projects/:id" element={<ProjectDetail />} />
            <Route path="worker" element={<WorkerStatus />} />
            <Route path="settings" element={<Settings />} />
            <Route path="*" element={<Navigate to="/" replace />} />
          </Route>
        </Routes>
      </BrowserRouter>
    </QueryClientProvider>
  )
}
