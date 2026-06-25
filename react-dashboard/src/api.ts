import type {
  Memory,
  ScoredMemory,
  RepositoryStats,
  HealthResponse,
  SystemStatus,
  CreateMemoryRequest,
  UpdateMemoryRequest,
  StoreResult,
  BrowseResponse,
  Project,
  CodeIndexFile,
  ProjectActivateResponse,
  ActiveProjectInfo,
  WorkerStatus,
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
  systemStatus: () => req<SystemStatus>('/api/admin/status'),
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

  projects: {
    list: () => req<Project[]>('/api/projects'),
    get: (id: string) => req<Project>(`/api/projects/${id}`),
    create: (name: string, rootPath: string) =>
      req<Project>('/api/projects', {
        method: 'POST',
        body: JSON.stringify({ name, rootPath }),
      }),
    delete: (id: string) => req<void>(`/api/projects/${id}`, { method: 'DELETE' }),
  },

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

  codeIndex: {
    getActive: () =>
      req<ActiveProjectInfo | null>('/api/codeindex/active').catch(() => null),

    activate: (projectId: string) =>
      req<ProjectActivateResponse>(`/api/projects/${projectId}/activate`, { method: 'POST' }),

    deactivate: () =>
      req<void>('/api/projects/active', { method: 'DELETE' }),

    workerStatus: () =>
      req<WorkerStatus>('/api/codeindex/worker/status'),

    listFiles: (projectId: string, search?: string) =>
      req<CodeIndexFile[]>(
        `/api/projects/${projectId}/files${search ? `?search=${encodeURIComponent(search)}` : ''}`
      ),

    reindex: (projectId: string) =>
      req<{ queued: number; alreadyCurrent: number }>(`/api/projects/${projectId}/reindex`, {
        method: 'POST',
      }),

    forceReindexAll: (projectId: string) =>
      req<{ queued: number; alreadyCurrent: number }>(`/api/projects/${projectId}/reindex?force=true`, {
        method: 'POST',
      }),

    getFile: (filePath: string) =>
      req<CodeIndexFile>(`/api/codeindex/file?path=${encodeURIComponent(filePath)}`),
  },
}
