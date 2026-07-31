/*
 * Enum-valued fields arrive as integers, not names.
 *
 * System.Text.Json writes a C# enum as its numeric value unless a converter says otherwise, and the
 * server registers none. `RetrievalConfidence` is the single exception, projected to a string before
 * it leaves the API. These were declared as string unions, which type-checks perfectly and matches
 * nothing at runtime: every `state === 'Archived'` was dead code against a payload carrying `2`.
 *
 * The label arrays are indexed by the value, so a new member added on the server shows as
 * `undefined` here rather than as the wrong name.
 */
export type MemoryVisibility = 0 | 1
export const VISIBILITY_LABELS = ['Global', 'Scoped'] as const

export type MemoryState = 0 | 1 | 2 | 3 | 4
export const STATE_LABELS = ['Active', 'Superseded', 'Archived', 'Forgotten', 'Merged'] as const

export type MemoryType = 0 | 1 | 2 | 3 | 4 | 5 | 6
export const TYPE_LABELS = [
  'Semantic', 'Identity', 'Preference', 'Persona', 'Episodic', 'Affective', 'Ephemeral',
] as const

export type MemorySource = 0 | 1 | 2 | 3
export const SOURCE_LABELS = [
  'User stated', 'Imported', 'System derived', 'Companion inferred',
] as const

export type Sensitivity = 0 | 1 | 2
export const SENSITIVITY_LABELS = ['Normal', 'Sensitive', 'Restricted'] as const

export type MemoryEventType = 0 | 1 | 2 | 3 | 4 | 5 | 6 | 7 | 8 | 9
export const EVENT_TYPE_LABELS = [
  'Created', 'Updated', 'Superseded', 'Archived', 'Restored',
  'Forgotten', 'Merged', 'Expired', 'Conflict recorded', 'Purged',
] as const

export type StoreAction = 0 | 1 | 2 | 3 | 4
export const STORE_ACTION_LABELS = [
  'Stored', 'Stored with supersede', 'Reinforced existing', 'Stored alongside', 'Stored with conflict',
] as const

/** The exception: a string on the wire. */
export type RetrievalConfidence = 'None' | 'Low' | 'Medium' | 'High'

/** Reads a label array safely, so an unknown value shows as itself rather than as `undefined`. */
export function label(labels: readonly string[], value: number): string {
  return labels[value] ?? `#${value}`
}

export interface Memory {
  id: string
  version: number

  // Scope — the privacy boundary.
  userId: string
  visibility: MemoryVisibility
  companionIds: string[]

  // Subject and slot — drive conflict resolution.
  subjectRef: string
  predicate?: string
  valueKey?: string

  type: MemoryType
  sensitivity: Sensitivity

  title: string
  summary: string
  content: string
  contentNormalized: string
  searchText: string
  verbatimQuote?: string
  tags: string[]

  // Provenance.
  source: MemorySource
  confidence: number
  conversationId?: string
  messageId?: string

  createdAt: string
  ingestedAt: string
  eventTime?: string
  lastAccessedAt: string
  validFrom: string
  validUntil?: string
  expiresAt?: string

  state: MemoryState
  /** Derived from `state`; retained for compatibility. */
  isArchived: boolean
  isCurrent: boolean
  isExpired: boolean

  baseStrength: number
  decayRate: number
  accessCount: number
  isPinned: boolean
  importance: number

  linkedNodeIds: string[]
  supersededBy?: string
  supersededIds: string[]
  mergedInto?: string

  embeddingModel?: string
  embeddingDim: number
}

export interface ScoredMemory {
  memory: Memory
  score: number
  fuzzyScore: number
  strengthScore: number
  recencyScore: number
  /** Null when this memory's vector was not comparable to the query's. */
  semanticScore: number | null
  matchedChannels: string[]
  isCoreContext: boolean
  /** Turns in which THIS companion has already drawn on the memory. 0 = new to her. */
  timesSurfacedToCompanion: number
  lastSurfacedToCompanionAt?: string
}

export type ConflictKind = 0 | 1 | 2 | 3 | 4 | 5
export const CONFLICT_KIND_LABELS = [
  'Value replaced', 'Soft preference change', 'Immutable violation',
  'Cross-scope contradiction', 'Provenance downgrade', 'Polarity contradiction',
] as const

/** Why each kind exists, for the one place a person has to make the call. */
export const CONFLICT_KIND_HELP: readonly string[] = [
  'Same singular slot, a different value. The newer one won.',
  'A soft preference changed. Both were kept; worth asking about.',
  'Something declared immutable, such as a birthday or legal name, was contradicted.',
  'A companion-scoped memory contradicts one that every companion shares. Resolving it the wrong way erases knowledge from all the others.',
  'A less trusted source tried to overwrite a more trusted one.',
  'One statement asserts what the other denies.',
]

export type ConflictStatus = 0 | 1 | 2
export const CONFLICT_STATUS_LABELS = ['Open', 'Resolved', 'Dismissed'] as const

export interface MemoryConflict {
  id: string
  userId: string
  newMemoryId: string
  existingMemoryId: string
  subjectRef: string
  predicate?: string
  kind: ConflictKind
  status: ConflictStatus
  detectedAt: string
  resolvedAt?: string
  winnerId?: string
  description: string
  companionId?: string
}

/** One side of a contradiction, reduced to what a decision needs. */
export interface ConflictSide {
  id: string
  title: string
  summary: string
  valueKey: string | null
  state: MemoryState
  source: MemorySource
  confidence: number
  createdAt: string
  validFrom: string
  isPinned: boolean
}

/**
 * A contradiction together with both memories it is about.
 *
 * Both sides come back with the conflict rather than being fetched by id afterwards. A forgotten
 * memory is deliberately unfetchable, so a side in that state could not be shown at all otherwise,
 * and "choose between this and something we will not show you" is not a question anyone can answer.
 * A side the current scope may not see is null.
 */
export interface ConflictDetail {
  conflict: MemoryConflict
  existing: ConflictSide | null
  new: ConflictSide | null
}

/** What `resolve` actually did. Names match the server's ConflictResolution. */
export interface ConflictResolveResult {
  outcome: 'Resolved' | 'Dismissed'
  winnerId: string | null
}

export interface MemoryEvent {
  id: string
  sequence: number
  userId: string
  memoryId: string
  type: MemoryEventType
  actor: string
  timestamp: string
  relatedMemoryId?: string
  memoryTitle?: string
  detail?: string
}

/** Envelope returned by POST /api/memory/search. */
export interface MemoryRetrievalResult {
  results: ScoredMemory[]
  coreContext: ScoredMemory[]
  conflicts: MemoryConflict[]
  confidence: RetrievalConfidence
  candidatesConsidered: number
  semanticSearchUsed: boolean
  /** Non-zero means a reindex is needed. */
  incomparableEmbeddings: number
}

/** A point-in-time copy of the store, taken before anything irreversible. */
export interface BackupSnapshot {
  path: string
  reason: string
  createdAt: string
  sizeBytes: number
}

/**
 * Where the server keeps things. `dataDirectory` is per-user and survives an application update;
 * `modelsDirectory` sits with the binary and is replaced by it.
 */
export interface StoragePaths {
  dataDirectory: string
  databasePath: string
  backupPath: string
  databaseBytes: number | null
  modelsDirectory: string
  embeddingsPath: string
  generativePath: string
  programDirectory: string
  /** How the data directory was chosen: CommandLine | Environment | Configuration | Portable | PlatformDefault. */
  origin: string
}

export interface MigrationHistoryEntry {
  fromVersion: number
  toVersion: number
  name: string
  documentsTouched: number
  appliedAt: string
  /** The application version that ran it — which release changed the data. */
  appVersion: string
}

/**
 * The database's account of itself. `schemaVersion` describes the shape of the stored data and
 * `appVersion` the build that is running; they move independently, so a host deciding whether an
 * older build may safely open this file has to compare `schemaVersion` against
 * `supportedSchemaVersion`, not the app versions.
 */
export interface DatabaseInfo {
  schemaVersion: number
  supportedSchemaVersion: number
  appVersion: string

  createdAt: string | null
  createdByAppVersion: string | null
  lastOpenedAt: string | null
  lastOpenedByAppVersion: string | null

  migratedOnThisStart: boolean
  migratedFromVersion: number
  snapshotPath: string | null

  history: MigrationHistoryEntry[]
}

export interface RepositoryStats {
  totalNodes: number
  activeNodes: number
  supersededNodes: number
  archivedNodes: number
  forgottenNodes: number
  openConflicts: number
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
  action: StoreAction
}

// ── Workspace / Sub-project ───────────────────────────────────────────────────

export type SubProjectType = 'CSharpProject' | 'TypeScript' | 'Node' | 'Python' | 'Unknown'

export interface SubProjectRecord {
  id: string
  workspaceId: string
  name: string
  rootPath: string
  type: SubProjectType
  manifestPath: string
  language: string
  namespace: string
  isProviderAvailable: boolean
}

export interface WorkspaceRecord {
  id: string
  name: string
  rootPath: string
  createdAt: string
  subProjects: SubProjectRecord[]
}

/** Backward-compat alias */
export type Project = WorkspaceRecord

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

export interface ParameterRecord {
  name: string
  type: string
  ordinal: number
  isOptional: boolean
  defaultValue?: string | null
  refKind: string
  isParams: boolean
  nullableAnnotation?: string | null
}

export interface EnumMemberRecord {
  name: string
  value?: number | null
  explicitExpression?: string | null
}

export interface AttributeRecord {
  name: string
  constructorArgs: string[]
  namedArgs: Record<string, string>
}

export interface ValidationRuleRecord {
  member: string
  rule: string
  args: Record<string, string>
}

export interface TypeParameterRecord {
  name: string
  constraints: string[]
  variance?: string | null
}

export interface SymbolRecord {
  name: string
  kind: string
  type: string | null
  accessibility: string
  line: number
  // P1 structured shape
  endLine: number
  containingTypeFullName?: string | null
  containingNamespace?: string | null
  symbolDocId?: string | null
  parameters: ParameterRecord[]
  returnTypeUnwrapped?: string | null
  modifiers: string[]
  isStatic: boolean
  isAbstract: boolean
  isSealed: boolean
  isVirtual: boolean
  isOverride: boolean
  isAsync: boolean
  enumMembers: EnumMemberRecord[]
  enumUnderlyingType?: string | null
  isFlags: boolean
  attributes: AttributeRecord[]
  constantValue?: string | null
  // P1 type-level contracts
  implementsIDisposable: boolean
  implementsIAsyncDisposable: boolean
  isBackgroundService: boolean
  hasStaticMutableState: boolean
  // P2 intent & contracts
  docSummary?: string | null
  docRemarks?: string | null
  paramDocs: Record<string, string>
  returnsDoc?: string | null
  documentedExceptions: string[]
  isDeprecated: boolean
  deprecationMessage?: string | null
  validationRules: ValidationRuleRecord[]
  nlDescription?: string | null
  isAwaitable: boolean
  isAsyncEnumerable: boolean
  usesLock: boolean
  blocksOnAsync: boolean
  usesInterlocked: boolean
  // P4 type structure
  typeParameters: TypeParameterRecord[]
  baseChain: string[]
  interfaces: string[]
  overriddenSymbolId?: string | null
  // P5 behavioral
  thrownExceptions: string[]
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
  // Symbol reference graph
  fanIn: number
  fanOut: number
  dependsOnFileIds: string[]
  usedByFileIds: string[]
  // Phase 4 semantic
  domainTags: string[]
  imports: string[]
  typeHierarchy: string[]
  diagnosticSummary: string
  // P1/P2/P6 file rollups
  isTestFile?: boolean
  testFramework?: string | null
  testSubjectFileIds?: string[]
  hasValidation?: boolean
  architecturalRole?: string | null
  isEntrypoint?: boolean
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

export interface SubProjectStatus {
  subProjectId: string
  name: string
  language: string
  indexedFiles: number
  staleFiles: number
  errorFiles: number
}

export interface QueuedFileEntry {
  relativePath: string
  filePath: string
}

export interface WorkerStatus {
  activeProjectId: string | null
  activeProjectName: string | null
  isProcessing: boolean
  currentFile: string | null
  currentSummaryFile: string | null
  queueDepth: number
  summaryQueueDepth: number
  totalIndexableFiles: number
  indexedFiles: number
  staleFiles: number
  errorFiles: number
  recentJobs: RecentJobEntry[]
  recentErrors: RecentErrorEntry[]
  subProjectStatuses: SubProjectStatus[]
  queuedIngestions: QueuedFileEntry[]
  queuedSummaries: QueuedFileEntry[]
  // Reference analysis worker
  currentReferenceFile: string | null
  referenceQueueDepth: number
  totalSymbolReferences: number
}

// ── Symbol reference graph ────────────────────────────────────────────────────

export interface SymbolUsageSite {
  fileId: string
  relativePath: string
  line: number
  context: string
  role?: string
  enclosingName?: string | null
}

export interface SymbolReference {
  id: string
  name: string
  kind: string
  accessibility: string
  definedInFileId: string
  definedInRelativePath: string
  definedAtLine: number
  fanIn: number
  usedBy: SymbolUsageSite[]
  testedByFileIds?: string[] | null
}

export interface SymbolSearchResult {
  total: number
  symbols: SymbolReference[]
}

export interface DependencyNode {
  id: string
  relativePath: string
  fanIn: number
  fanOut: number
  symbolCount: number
  language: string
}

export interface DependencyEdge {
  from: string
  to: string
  viaSymbols: string[]
}

export interface DependencyGraph {
  nodes: DependencyNode[]
  edges: DependencyEdge[]
}

export interface IntelligenceFileProfile {
  file: CodeIndexFile
  definedSymbols: SymbolReference[]
  dependsOn: DependencyNode[]
}

// ── P0–P6 surfacing ──────────────────────────────────────────────────────────

export interface IntelligenceOverview {
  files: number
  symbols: number
  endpoints: number
  diEdges: number
  efEntities: number
  mediatrMessages: number
  typeRelations: number
  configKeys: number
  securitySinks: number
  testFiles: number
  packages: number
}

export interface FileContent {
  fileId: string
  relativePath: string
  startLine: number
  endLine: number
  totalLines: number
  stale: boolean
  content: string
}

export interface DomainFact {
  kind: string
  line: number
  method?: string | null
  route?: string | null
  name?: string | null
  typeRef?: string | null
  ownerType?: string | null
  items: string[]
  fileId: string
  relativePath: string
}

export interface PackageDependency {
  name: string
  version: string
  isDev: boolean
}

export interface ProjectManifest {
  manifestType: string
  manifestPath: string
  targetFrameworks: string[]
  outputKind?: string | null
  langVersion?: string | null
  nullable?: string | null
  implicitUsings: boolean
  packages: PackageDependency[]
  projectReferences: string[]
  scripts: Record<string, string>
}

export interface SemanticSymbolHit {
  id: string
  symbolName: string
  containingType?: string | null
  kind: string
  fileId: string
  relativePath: string
  line: number
  endLine: number
  score: number
}
