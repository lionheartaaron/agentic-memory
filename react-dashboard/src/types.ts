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
