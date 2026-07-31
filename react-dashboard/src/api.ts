import type {
  Memory,
  MemoryRetrievalResult,
  ConflictDetail,
  ConflictResolveResult,
  MemoryEvent,
  RepositoryStats,
  BackupSnapshot,
  StoragePaths,
  DatabaseInfo,
  HealthResponse,
  SystemStatus,
  CreateMemoryRequest,
  UpdateMemoryRequest,
  StoreResult,
  BrowseResponse,
  WorkspaceRecord,
  CodeIndexFile,
  ProjectActivateResponse,
  ActiveProjectInfo,
  WorkerStatus,
  SymbolSearchResult,
  DependencyGraph,
  DependencyNode,
  IntelligenceFileProfile,
  IntelligenceOverview,
  FileContent,
  DomainFact,
  ProjectManifest,
  SemanticSymbolHit,
} from './types'

/** Builds a query string, omitting undefined/empty values. */
function qs(params: Record<string, string | number | boolean | undefined>): string {
  const entries = Object.entries(params).filter(
    ([, v]) => v !== undefined && v !== '',
  ) as [string, string | number | boolean][]

  if (!entries.length) return ''
  return '?' + entries.map(([k, v]) => `${k}=${encodeURIComponent(String(v))}`).join('&')
}

const API_KEY_STORAGE = 'agenticMemoryApiKey'
const USER_ID_STORAGE = 'agenticMemoryUserId'

/**
 * Whose memories the dashboard is looking at.
 *
 * Every memory endpoint is scoped, and a request that names no user falls back to the one called
 * "default". On a store an agent writes to under its own user id, that shows an empty dashboard
 * with no indication anything is being hidden: the memories are there, the question was just asked
 * about somebody else. Held here and applied to every scoped call, rather than threaded through
 * each page, so nothing can forget to send it.
 *
 * Empty means the default user, which is the right answer for a single-user store.
 */
export function getUserId(): string {
  try {
    return localStorage.getItem(USER_ID_STORAGE) ?? ''
  } catch {
    return ''
  }
}

export function setUserId(id: string): void {
  try {
    if (id) localStorage.setItem(USER_ID_STORAGE, id)
    else localStorage.removeItem(USER_ID_STORAGE)
  } catch {
    /* nothing useful to do */
  }
}

/** The current scope as query parameters, dropped entirely when it is the default user. */
function scope(extra: Record<string, string | number | boolean | undefined> = {}) {
  return qs({ userId: getUserId() || undefined, ...extra })
}

/**
 * The server's API key, when it has one configured.
 *
 * Kept in localStorage rather than baked into the bundle: the dashboard is static and identical for
 * every install, so anything compiled in would ship the secret to everyone. A browser also cannot
 * attach a header to its own page load, which is why the static assets are unauthenticated and only
 * the `/api` calls below carry the key.
 */
export function getApiKey(): string {
  try {
    return localStorage.getItem(API_KEY_STORAGE) ?? ''
  } catch {
    return '' // private browsing, or storage disabled
  }
}

export function setApiKey(key: string): void {
  try {
    if (key) localStorage.setItem(API_KEY_STORAGE, key)
    else localStorage.removeItem(API_KEY_STORAGE)
  } catch {
    /* nothing useful to do; requests will 401 and say so */
  }
}

/** Thrown when the server requires a key and did not get a valid one. */
export class UnauthorizedError extends Error {
  constructor(message = 'This server requires an API key.') {
    super(message)
    this.name = 'UnauthorizedError'
  }
}

async function req<T>(url: string, init?: RequestInit): Promise<T> {
  const key = getApiKey()

  const res = await fetch(url, {
    headers: {
      'Content-Type': 'application/json',
      ...(key ? { 'X-API-Key': key } : {}),
      ...init?.headers,
    },
    ...init,
  })

  // Distinguished from a generic failure so the UI can prompt for a key rather than reporting
  // the server as broken.
  if (res.status === 401) {
    const body = await res.json().catch(() => null)
    throw new UnauthorizedError(body?.error)
  }

  if (!res.ok) {
    // The endpoints that reject a request explain why in the body. "400 Bad Request" on its own
    // tells a user nothing they can act on, and these messages exist precisely to be acted on.
    const body = await res.json().catch(() => null)
    throw new Error(body?.error ?? `${res.status} ${res.statusText}`)
  }

  if (res.status === 204) return undefined as T
  return res.json() as Promise<T>
}

export const api = {
  health: () => req<HealthResponse>('/api/admin/health'),
  stats: () => req<RepositoryStats>('/api/admin/stats'),
  systemStatus: () => req<SystemStatus>('/api/admin/status'),
  generateStatus: () => req<{ available: boolean }>('/api/generate/status'),

  list: (includeArchived = false) =>
    req<Memory[]>(`/api/memory${scope({ includeArchived: includeArchived || undefined })}`),

  get: (id: string) => req<Memory>(`/api/memory/${id}${scope()}`),

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

  delete: (id: string) => req<void>(`/api/memory/${id}${scope()}`, { method: 'DELETE' }),

  search: (
    query: string,
    topN = 20,
    tags?: string[],
    userId?: string,
    companionId?: string,
    opts?: { asOf?: string; noveltyBias?: number },
  ) =>
    req<MemoryRetrievalResult>('/api/memory/search', {
      method: 'POST',
      body: JSON.stringify({
        query,
        topN,
        tags: tags?.length ? tags : undefined,
        userId: userId ?? (getUserId() || undefined),
        companionId,
        asOf: opts?.asOf,
        noveltyBias: opts?.noveltyBias,
      }),
    }),

  /** Where the database, snapshots and models actually are on disk. */
  paths: () => req<StoragePaths>('/api/admin/paths'),

  /** Schema version, app version, and every migration this database has been through. */
  database: () => req<DatabaseInfo>('/api/admin/database'),

  backups: () => req<BackupSnapshot[]>('/api/admin/backups'),

  createBackup: () => req<BackupSnapshot>('/api/admin/backups', { method: 'POST' }),

  /** `openOnly` defaults to true server-side, so the settled ones need asking for. */
  conflicts: (openOnly = true, companionId?: string) =>
    req<ConflictDetail[]>(`/api/memory/conflicts${scope({ openOnly, companionId })}`),

  conflict: (id: string) => req<ConflictDetail>(`/api/memory/conflicts/${id}${scope()}`),

  // The scope goes in the body here, not the query string, because that is where
  // ConflictResolveRequest carries it. Sending it is not optional: without it the lookup runs
  // against the default user and a conflict belonging to anyone else comes back 404.
  resolveConflict: (id: string, winnerId?: string, dismiss = false) =>
    req<ConflictResolveResult>(`/api/memory/conflicts/${id}/resolve`, {
      method: 'POST',
      body: JSON.stringify({ winnerId, dismiss, userId: getUserId() || undefined }),
    }),

  /** Unscoped on the server: the event log is keyed by memory id alone. */
  history: (id: string) => req<MemoryEvent[]>(`/api/memory/${id}/history`),

  restore: (id: string) =>
    req<void>(`/api/memory/${id}/restore${scope()}`, { method: 'POST' }),

  slotHistory: (predicate: string, subject = 'user', companionId?: string) =>
    req<Memory[]>(`/api/memory/slot${scope({ predicate, subject, companionId })}`),

  fileContext: (filePath: string) =>
    req<{ context: string }>(`/api/file/context?path=${encodeURIComponent(filePath)}`),

  fileSummary: (filePath: string) =>
    req<{ summary: string }>('/api/file/summary', {
      method: 'POST',
      body: JSON.stringify({ filePath }),
    }),

  browsePath: (path?: string) =>
    req<BrowseResponse>(`/api/files/browse${path ? `?path=${encodeURIComponent(path)}` : ''}`),

  // Workspace-aware project APIs (backward compat aliases on /api/projects/* still work)
  projects: {
    list: () => req<WorkspaceRecord[]>('/api/workspaces'),
    get: (id: string) => req<WorkspaceRecord>(`/api/workspaces/${id}`),
    create: (name: string, rootPath: string) =>
      req<WorkspaceRecord>('/api/workspaces', {
        method: 'POST',
        body: JSON.stringify({ name, rootPath }),
      }),
    delete: (id: string) => req<void>(`/api/workspaces/${id}`, { method: 'DELETE' }),
  },

  workspaces: {
    list: () => req<WorkspaceRecord[]>('/api/workspaces'),
    get: (id: string) => req<WorkspaceRecord>(`/api/workspaces/${id}`),
    create: (name: string, rootPath: string) =>
      req<WorkspaceRecord>('/api/workspaces', {
        method: 'POST',
        body: JSON.stringify({ name, rootPath }),
      }),
    delete: (id: string) => req<void>(`/api/workspaces/${id}`, { method: 'DELETE' }),
    discover: (id: string) =>
      req<WorkspaceRecord>(`/api/workspaces/${id}/discover`, { method: 'POST' }),
    activate: (id: string) =>
      req<ProjectActivateResponse>(`/api/workspaces/${id}/activate`, { method: 'POST' }),
    listFiles: (id: string, search?: string, subProjectId?: string) => {
      const params = new URLSearchParams()
      if (search) params.set('search', search)
      if (subProjectId) params.set('subProjectId', subProjectId)
      const qs = params.toString()
      return req<CodeIndexFile[]>(`/api/workspaces/${id}/files${qs ? `?${qs}` : ''}`)
    },
    reindex: (id: string) =>
      req<{ queued: number; alreadyCurrent: number }>(`/api/workspaces/${id}/reindex`, {
        method: 'POST',
      }),
    reindexSubProject: (id: string, subProjectId: string) =>
      req<{ queued: number; alreadyCurrent: number }>(
        `/api/workspaces/${id}/sub-projects/${subProjectId}/reindex`,
        { method: 'POST' }
      ),
    staleFiles: (id: string) =>
      req<CodeIndexFile[]>(`/api/workspaces/${id}/stale-files`),
    errorFiles: (id: string) =>
      req<CodeIndexFile[]>(`/api/workspaces/${id}/error-files`),
  },

  admin: {
    /** Every user id the store has memories for. Feeds the scope selector. */
    users: () => req<string[]>('/api/admin/users'),

    maintenanceStats: () =>
      req<{ memories: number; codeIndexFiles: number; workspaces: number; dbSizeBytes: number }>(
        '/api/admin/maintenance-stats'
      ),
    clearCodeIndex: () => req<void>('/api/admin/code-index', { method: 'DELETE' }),
    clearMemories: () => req<void>('/api/admin/memories', { method: 'DELETE' }),
    clearWorkspaces: () => req<void>('/api/admin/workspaces', { method: 'DELETE' }),
    fullReset: () => req<void>('/api/admin/full-reset', { method: 'POST' }),
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

  intelligence: {
    listSymbols: (
      projectId: string,
      opts?: {
        q?: string
        kind?: string
        publicOnly?: boolean
        minFanIn?: number
        subProjectId?: string
        offset?: number
        limit?: number
      }
    ) => {
      const params = new URLSearchParams()
      if (opts?.q)           params.set('q', opts.q)
      if (opts?.kind)        params.set('kind', opts.kind)
      if (opts?.publicOnly)  params.set('publicOnly', 'true')
      if (opts?.minFanIn != null) params.set('minFanIn', String(opts.minFanIn))
      if (opts?.subProjectId)     params.set('subProjectId', opts.subProjectId)
      if (opts?.offset != null)   params.set('offset', String(opts.offset))
      if (opts?.limit  != null)   params.set('limit',  String(opts.limit))
      const qs = params.toString()
      return req<SymbolSearchResult>(
        `/api/workspaces/${projectId}/intelligence/symbols${qs ? `?${qs}` : ''}`
      )
    },

    getGraph: (projectId: string, subProjectId?: string) => {
      const qs = subProjectId ? `?subProjectId=${encodeURIComponent(subProjectId)}` : ''
      return req<DependencyGraph>(`/api/workspaces/${projectId}/intelligence/graph${qs}`)
    },

    getHotspots: (projectId: string, topN = 20) =>
      req<DependencyNode[]>(
        `/api/workspaces/${projectId}/intelligence/hotspots?topN=${topN}`
      ),

    getEntrypoints: (projectId: string) =>
      req<DependencyNode[]>(`/api/workspaces/${projectId}/intelligence/entrypoints`),

    getFileProfile: (projectId: string, fileId: string) =>
      req<IntelligenceFileProfile>(
        `/api/workspaces/${projectId}/intelligence/file/${encodeURIComponent(fileId)}`
      ),

    getOverview: (projectId: string, subProjectId?: string) => {
      const qs = subProjectId ? `?subProjectId=${encodeURIComponent(subProjectId)}` : ''
      return req<IntelligenceOverview>(`/api/workspaces/${projectId}/intelligence/overview${qs}`)
    },

    getContent: (projectId: string, fileId: string, startLine?: number, endLine?: number) => {
      const params = new URLSearchParams()
      if (startLine != null) params.set('startLine', String(startLine))
      if (endLine != null)   params.set('endLine', String(endLine))
      const qs = params.toString()
      return req<FileContent>(
        `/api/workspaces/${projectId}/intelligence/file/${encodeURIComponent(fileId)}/content${qs ? `?${qs}` : ''}`
      )
    },

    getSymbolContent: (projectId: string, fileId: string, name: string) =>
      req<FileContent>(
        `/api/workspaces/${projectId}/intelligence/file/${encodeURIComponent(fileId)}/symbol/${encodeURIComponent(name)}`
      ),

    getDomainFacts: (projectId: string, kind?: string, subProjectId?: string) => {
      const params = new URLSearchParams()
      if (kind)         params.set('kind', kind)
      if (subProjectId) params.set('subProjectId', subProjectId)
      const qs = params.toString()
      return req<DomainFact[]>(
        `/api/workspaces/${projectId}/intelligence/domain-facts${qs ? `?${qs}` : ''}`
      )
    },

    getManifests: (projectId: string) =>
      req<ProjectManifest[]>(`/api/workspaces/${projectId}/intelligence/manifests`),

    searchSemantic: (projectId: string, q: string, topN = 20, subProjectId?: string) => {
      const params = new URLSearchParams({ q })
      params.set('topN', String(topN))
      if (subProjectId) params.set('subProjectId', subProjectId)
      return req<SemanticSymbolHit[]>(
        `/api/workspaces/${projectId}/intelligence/semantic?${params.toString()}`
      )
    },
  },

  codeIndex: {
    getActive: () =>
      req<ActiveProjectInfo | null>('/api/codeindex/active').catch(() => null),

    activate: (projectId: string) =>
      req<ProjectActivateResponse>(`/api/workspaces/${projectId}/activate`, { method: 'POST' }),

    deactivate: () =>
      req<void>('/api/projects/active', { method: 'DELETE' }),

    workerStatus: () =>
      req<WorkerStatus>('/api/codeindex/worker/status'),

    listFiles: (projectId: string, search?: string, subProjectId?: string) => {
      const params = new URLSearchParams()
      if (search) params.set('search', search)
      if (subProjectId) params.set('subProjectId', subProjectId)
      const qs = params.toString()
      return req<CodeIndexFile[]>(`/api/projects/${projectId}/files${qs ? `?${qs}` : ''}`)
    },

    reindex: (projectId: string) =>
      req<{ queued: number; alreadyCurrent: number }>(`/api/workspaces/${projectId}/reindex`, {
        method: 'POST',
      }),

    forceReindexAll: (projectId: string) =>
      req<{ queued: number; alreadyCurrent: number }>(`/api/projects/${projectId}/reindex?force=true`, {
        method: 'POST',
      }),

    reindexSubProject: (workspaceId: string, subProjectId: string) =>
      req<{ queued: number; alreadyCurrent: number }>(
        `/api/workspaces/${workspaceId}/sub-projects/${subProjectId}/reindex`,
        { method: 'POST' }
      ),

    getFile: (filePath: string) =>
      req<CodeIndexFile>(`/api/codeindex/file?path=${encodeURIComponent(filePath)}`),
  },
}
