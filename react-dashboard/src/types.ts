export interface Memory {
  id: string
  title: string
  summary: string
  content: string
  contentNormalized: string
  tags: string[]
  createdAt: string
  lastAccessedAt: string
  baseStrength: number
  decayRate: number
  accessCount: number
  isArchived: boolean
  isCurrent: boolean
  isPinned: boolean
  importance: number
  linkedNodeIds: string[]
  supersededBy?: string
  supersededIds: string[]
  validFrom: string
  validUntil?: string
  expiresAt?: string
}

export interface ScoredMemory {
  memory: Memory
  score: number
  fuzzyScore: number
  strengthScore: number
  recencyScore: number
  semanticScore: number
}

export interface RepositoryStats {
  totalNodes: number
  averageStrength: number
  weakMemoriesCount: number
  oldestMemory?: string
  newestMemory?: string
  databaseSizeBytes: number
}

export interface HealthResponse {
  status: string
  timestamp: string
}

export interface SystemStatus {
  status: 'healthy' | string
  timestamp: string
  server: {
    listeningUrl: string
  }
  generation: {
    enabled: boolean
    available: boolean
    modelName: string | null
  }
  embeddings: {
    enabled: boolean
    available: boolean
    modelName: string | null
    dimensions: number
  }
  maintenance: {
    enabled: boolean
  }
  codeIndex: {
    enabled: boolean
    providers: Array<{
      providerType: string
      compilerApi: string
      domainPatternFamilies: string[]
      active: boolean
    }>
  }
}

export interface CreateMemoryRequest {
  title: string
  summary: string
  content?: string
  tags?: string[]
  importance?: number
}

export interface UpdateMemoryRequest {
  title?: string
  summary?: string
  content?: string
  tags?: string[]
}

export interface StoreResult {
  memory: Memory
  action: string
}

export interface Project {
  id: string
  name: string
  rootPath: string
  createdAt: string
}

export interface FsItem {
  name: string
  fullPath: string
  isDirectory: boolean
  extension: string | null
}

export interface BrowseResponse {
  path: string
  parent: string | null
  items: FsItem[]
}

// ── Code Index Brain ──────────────────────────────────────────────────────────

export interface SymbolRecord {
  name: string
  kind: string
  type: string | null
  accessibility: string
  line: number
}

export interface CodeIndexFile {
  id: string
  projectId: string
  filePath: string
  fileName: string
  relativePath: string
  language: string
  providerType: string
  extractedContext: string
  llmSummary: string
  symbols: SymbolRecord[]
  indexedAt: string
  fileModifiedAt: string
  isStale: boolean
  ingestionError: string | null
  score?: number | null
}

export interface ProjectActivateResponse {
  projectId: string
  name: string
  rootPath: string
  queuedFiles: number
  alreadyIndexed: number
}

export interface ActiveProjectInfo {
  projectId: string
  name: string
  rootPath: string
}

export interface RecentJobEntry {
  relativePath: string
  language: string
  symbolCount: number
  durationMs: number
  indexedAt: string
  wasNew: boolean
}

export interface RecentErrorEntry {
  relativePath: string
  error: string
  occurredAt: string
}

export interface WorkerStatus {
  activeProjectId: string | null
  activeProjectName: string | null
  isProcessing: boolean
  currentFile: string | null
  queueDepth: number
  summaryQueueDepth: number
  totalIndexableFiles: number
  indexedFiles: number
  staleFiles: number
  errorFiles: number
  recentJobs: RecentJobEntry[]
  recentErrors: RecentErrorEntry[]
}
