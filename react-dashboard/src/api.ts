import type {
  Memory,
  ScoredMemory,
  RepositoryStats,
  HealthResponse,
  CreateMemoryRequest,
  UpdateMemoryRequest,
  StoreResult,
  BrowseResponse,
} from './types'

async function req<T>(url: string, init?: RequestInit): Promise<T> {
  const res = await fetch(url, {
    headers: { 'Content-Type': 'application/json', ...init?.headers },
    ...init,
  })
  if (!res.ok) throw new Error(`${res.status} ${res.statusText}`)
  if (res.status === 204) return undefined as T
  return res.json() as Promise<T>
}

export const api = {
  health: () => req<HealthResponse>('/api/admin/health'),
  stats: () => req<RepositoryStats>('/api/admin/stats'),
  generateStatus: () => req<{ available: boolean }>('/api/generate/status'),

  list: (includeArchived = false) =>
    req<Memory[]>(`/api/memory${includeArchived ? '?includeArchived=true' : ''}`),

  get: (id: string) => req<Memory>(`/api/memory/${id}`),

  create: (data: CreateMemoryRequest) =>
    req<StoreResult>('/api/memory', {
      method: 'POST',
      body: JSON.stringify(data),
    }),

  update: (id: string, data: UpdateMemoryRequest) =>
    req<Memory>(`/api/memory/${id}`, {
      method: 'PUT',
      body: JSON.stringify(data),
    }),

  delete: (id: string) => req<void>(`/api/memory/${id}`, { method: 'DELETE' }),

  search: (query: string, topN = 20, tags?: string[]) =>
    req<ScoredMemory[]>('/api/memory/search', {
      method: 'POST',
      body: JSON.stringify({
        query,
        topN,
        tags: tags?.length ? tags : undefined,
      }),
    }),

  fileContext: (filePath: string) =>
    req<{ context: string }>(`/api/file/context?path=${encodeURIComponent(filePath)}`),

  fileSummary: (filePath: string) =>
    req<{ summary: string }>('/api/file/summary', {
      method: 'POST',
      body: JSON.stringify({ filePath }),
    }),

  browsePath: (path?: string) =>
    req<BrowseResponse>(`/api/files/browse${path ? `?path=${encodeURIComponent(path)}` : ''}`),

  kv: {
    get: (key: string) =>
      req<{ value: string | null }>(`/api/kv/${encodeURIComponent(key)}`),
    set: (key: string, value: string) =>
      req<void>(`/api/kv/${encodeURIComponent(key)}`, {
        method: 'PUT',
        body: JSON.stringify({ value }),
      }),
    delete: (key: string) =>
      req<void>(`/api/kv/${encodeURIComponent(key)}`, { method: 'DELETE' }),
  },
}
