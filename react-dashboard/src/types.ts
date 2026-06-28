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
  hasUnusedPublicSymbols?: boolean
  orphanSymbolCount?: number
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
  isOrphan?: boolean
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
  orphanSymbols: number
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
