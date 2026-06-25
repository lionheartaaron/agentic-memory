import { BrowserRouter, Routes, Route, Navigate } from 'react-router-dom'
import { QueryClient, QueryClientProvider } from '@tanstack/react-query'
import Layout from './components/Layout'
import Overview from './pages/Overview'
import Browse from './pages/Browse'
import MemoryDetail from './pages/MemoryDetail'
import Chat from './pages/Chat'
import Projects from './pages/Projects'
import ProjectDetail from './pages/ProjectDetail'
import WorkerStatus from './pages/WorkerStatus'

const queryClient = new QueryClient({
  defaultOptions: {
    queries: {
      staleTime: 10_000,
      retry: 1,
    },
  },
})

export default function App() {
  return (
    <QueryClientProvider client={queryClient}>
      <BrowserRouter>
        <Routes>
          <Route path="/" element={<Layout />}>
            <Route index element={<Overview />} />
            <Route path="memories" element={<Browse />} />
            <Route path="memories/:id" element={<MemoryDetail />} />
            <Route path="chat" element={<Chat />} />
            <Route path="projects" element={<Projects />} />
            <Route path="projects/:id" element={<ProjectDetail />} />
            <Route path="worker" element={<WorkerStatus />} />
            <Route path="*" element={<Navigate to="/" replace />} />
          </Route>
        </Routes>
      </BrowserRouter>
    </QueryClientProvider>
  )
}
