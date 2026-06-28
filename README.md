<div align="center">

# Agentic Memory

### **A local-first context engine for AI agents — memory, code intelligence, and an on-device LLM**

[![.NET 10](https://img.shields.io/badge/.NET-10.0-512BD4?style=flat&logo=dotnet)](https://dotnet.microsoft.com/)
[![MCP Compatible](https://img.shields.io/badge/MCP-Compatible-green?style=flat)](https://modelcontextprotocol.io/)
[![Local First](https://img.shields.io/badge/Local-First-blue?style=flat)]()
[![License: MIT](https://img.shields.io/badge/License-MIT-yellow.svg)](https://opensource.org/licenses/MIT)

*Persistent memory, compiler-grade code understanding, and a built-in generative model — all running on your machine, exposed to any MCP-compatible agent.*

[Quick Start](#quick-start) • [The Three Pillars](#the-three-pillars) • [Dashboard](#dashboard) • [Connect an Agent](#connect-an-agent) • [MCP Tools](#mcp-tools) • [Configuration](#configuration)

</div>

---

## What is this?

Agentic Memory started as a memory layer for AI assistants. It's now a **local AI context platform** with three tightly-integrated subsystems, a single embedded database, and zero cloud dependencies:

| | Pillar | What it gives your agent |
|---|---|---|
| 🧠 | **Memory Engine** | Persistent, semantically-searchable memory that strengthens with use and fades when forgotten |
| 🔍 | **Code Intelligence** | Compiler-backed understanding of C#, TypeScript & React codebases — symbols, references, dependency graphs, API surfaces |
| ✨ | **Local Generative AI** | An on-device LLM (Phi-4-mini) for code summaries and chat — no API keys, no data leaving your machine |

Everything is reachable three ways: the **MCP server** (for agents), a **REST API** (for custom tooling), and a **React dashboard** (for humans). The embedding model, the generative model, the C# compiler (Roslyn), and the TypeScript compiler all run **in-process and offline**.

```mermaid
flowchart LR
    subgraph Agents["AI Agents & Tools"]
        CP[Copilot]
        CL[Claude]
        CU[Cursor]
        UI[Dashboard]
    end

    subgraph Server["Agentic Memory · localhost:3377"]
        MCP[MCP Server]
        API[REST API]
    end

    subgraph Engine["Local Engine — 100% offline"]
        MEM[🧠 Memory<br/>semantic search · decay]
        CODE[🔍 Code Intelligence<br/>Roslyn · TypeScript]
        GEN[✨ Phi-4-mini LLM<br/>summaries · chat]
        DB[(LiteDB)]
    end

    CP & CL & CU <--> MCP
    UI <--> API
    MCP & API <--> MEM & CODE & GEN
    MEM & CODE <--> DB
```

---

## The Three Pillars

### 🧠 Memory Engine

A memory store that behaves more like human recall than a key-value cache.

- **Semantic + fuzzy search** — ONNX SBERT embeddings (`all-MiniLM-L6-v2`, 384-dim) combined with trigram matching for typo tolerance. Results rank on semantic similarity, fuzzy overlap, strength, and recency.
- **Reinforcement & decay** — accessing a memory strengthens it (with diminishing returns); unused memories decay exponentially and are eventually pruned. `Importance` slows decay; pinned memories never fade.
- **Conflict resolution on write** — new memories are checked against existing ones: near-duplicates reinforce, contradictions supersede (the old version is archived, not lost), related facts coexist.
- **Auto-consolidation** — background maintenance clusters and merges similar memories to reduce clutter.
- **Temporal history** — superseded memories are archived with validity ranges, so you can replay how a fact evolved over time.

### 🔍 Code Intelligence

Not regex or ctags — this routes every query through **real compiler APIs** for accurate, whole-program understanding.

- **C# via Roslyn** — a whole-program `CSharpCompilation` resolves types, inheritance chains, and symbol identity. Extracts symbols, signatures, modifiers, attributes, XML docs, and an inverted reference index for fast find-all-references and call-graph attribution.
- **TypeScript & React via the real TS compiler** — the actual `typescript.js` LanguageService runs in-process inside a V8 engine (ClearScript). Whole-program type resolution across barrel re-exports, semantic diagnostics, symbols, and references.
- **Framework-aware "domain facts"** — detects ASP.NET routes & DI, EF Core entities, MediatR handlers, React hooks, TanStack Query/Mutation chains, fetch() API clients, and navigation edges.
- **Dependency & dead-code analysis** — fan-in/fan-out metrics, hotspots, entry points, a full dependency graph, orphan (unused public symbol) detection, and test-to-subject linkage.
- **Incremental & background** — a file watcher ingests changes; dedicated workers handle indexing, reference-graph building, and summarization without blocking.

### ✨ Local Generative AI

A genuine LLM running on your CPU, no GPU or cloud required.

- **Model** — Microsoft `Phi-4-mini-instruct`, int4-quantized ONNX (128K context, ~5 GB), served via ONNX Runtime GenAI.
- **Uses** — generates concise file/symbol summaries that feed semantic search, and powers the dashboard chat (with streaming via Server-Sent Events).
- **Opt-in** — disabled by default; flip `Generation.Enabled` to `true` to auto-download and enable it. Everything else works without it.

---

## Quick Start

### Prerequisites
- **.NET 10 SDK**
- **Node.js + npm** — the build compiles the React dashboard automatically

### 1. Clone & build
```bash
git clone https://github.com/lionheartaaron/agentic-memory.git
cd agentic-memory
dotnet build
```
> Building runs `npm install && npm run build` for the dashboard (via an MSBuild target) and emits it into `wwwroot/`.

### 2. Run the server
```bash
dotnet run --project agentic-memory
```

### 3. You're live
```
   Agentic Memory Server
   Dashboard:     http://localhost:3377
   MCP Endpoint:  http://localhost:3377/mcp
   Status:        Ready for connections
```

The embedding model (~90 MB) downloads automatically on first run. The optional generative model (~5 GB) downloads only if you enable `Generation`.

---

## Dashboard

Open **http://localhost:3377** for a React + Vite + Tailwind UI that exposes the whole system:

- **Overview** — memory stats, strength distribution, system health
- **Memories** — search, browse, create/edit, inspect strength and history
- **Workspaces** — register a codebase, auto-discover sub-projects, watch indexing progress
- **Code Intelligence** — dependency graph, hotspots & entry points, symbol search, domain facts (endpoints, entities, etc.), per-file LLM summaries
- **Worker Status** — live indexing, summary, and reference-graph queues
- **Chat** — talk to the local Phi-4-mini model with streaming responses
- **Settings** — maintenance and database reset operations

The dashboard is pre-built into `wwwroot/` and served as static files by the .NET server — no separate process to run.

---

## Connect an Agent

Any [Model Context Protocol](https://modelcontextprotocol.io/) client connects to:

```
http://localhost:3377/mcp
```

<details>
<summary><b>GitHub Copilot (VS Code)</b></summary>

Create `.vscode/mcp.json` in your workspace:
```json
{
  "servers": {
    "agentic-memory": { "type": "http", "url": "http://localhost:3377/mcp" }
  }
}
```
Add it to VS Code User Settings instead to enable it for every workspace.
</details>

<details>
<summary><b>Claude Desktop</b></summary>

Add to `claude_desktop_config.json` (macOS: `~/Library/Application Support/Claude/`, Windows: `%APPDATA%\Claude\`):
```json
{
  "mcpServers": {
    "agentic-memory": { "url": "http://localhost:3377/mcp" }
  }
}
```
Restart Claude Desktop to connect.
</details>

<details>
<summary><b>Cursor</b></summary>

Create `.cursor/mcp.json`:
```json
{
  "mcpServers": {
    "agentic-memory": { "url": "http://localhost:3377/mcp" }
  }
}
```
</details>

<details>
<summary><b>Agent instructions (recommended)</b></summary>

Drop something like this into your agent's system/instructions file (e.g. `.github/copilot-instructions.md`) so it actually uses the memory:

```markdown
You have a persistent memory system via the `agentic-memory` MCP server.

- **Search memories** at the start of recurring topics, before making
  recommendations, or when asked about past decisions.
- **Store memories** when the user asks you to remember something, or when an
  important preference, decision, or technical pattern is established.
- **Always search before storing** to avoid duplicates — update instead of duplicating.
- Use `search_code` / `get_project_info` to ground answers in the indexed codebase.
```
</details>

---

## MCP Tools

The MCP server exposes memory operations plus code-index access:

| Tool | Description |
|------|-------------|
| `search_memories` | Semantic + fuzzy search over memories (`query`, `top_n`, `tags`) |
| `store_memory` | Create a memory with automatic conflict resolution |
| `update_memory` | Update an existing memory by ID |
| `get_memory` | Fetch a memory by ID (and reinforce it) |
| `delete_memory` | Permanently delete a memory |
| `get_stats` | Memory system statistics |
| `get_tag_history` | Temporal history for a tag, including superseded memories |
| `get_project_info` | List registered workspaces and discovered sub-projects |
| `search_code` | Semantic + keyword search over the indexed codebase |

---

## REST API

The dashboard runs entirely on a documented HTTP API — handy for custom integrations. Highlights:

| Area | Endpoints |
|------|-----------|
| **Memory** | `GET/POST /api/memory`, `GET/PUT/DELETE /api/memory/{id}`, `POST /api/memory/search` |
| **Workspaces** | `GET/POST /api/workspaces`, `POST /api/workspaces/{id}/discover`, `.../activate`, `.../reindex` |
| **Code Intelligence** | `GET /api/workspaces/{id}/intelligence/{symbols,hotspots,entrypoints,graph,domain-facts,overview,semantic}` |
| **Generation** | `POST /api/generate`, `POST /api/generate/stream` (SSE), `GET /api/generate/status` |
| **Files** | `GET /api/files/browse`, `GET /api/file/context`, `POST /api/file/summary` |
| **Admin** | `GET /api/admin/{status,stats,health}`, `DELETE /api/admin/{memories,code-index,workspaces}`, `POST /api/admin/full-reset` |

<details>
<summary><b>Example: store and search memories via curl</b></summary>

```bash
# Store
curl -X POST http://localhost:3377/api/memory \
  -H "Content-Type: application/json" \
  -d '{
    "title": "User prefers dark themes",
    "summary": "Always suggest dark mode configurations",
    "content": "The user prefers dark themes across IDEs, terminals, and web apps.",
    "tags": ["user-preference", "ui"],
    "importance": 0.8
  }'

# Search
curl -X POST http://localhost:3377/api/memory/search \
  -H "Content-Type: application/json" \
  -d '{ "query": "ui preferences", "topN": 5 }'
```
</details>

---

## How It Works

**Memory scoring.** When embeddings are available, results rank as
`semantic × 0.4 + fuzzy × 0.3 + strength × 0.2 + recency × 0.1`
(falling back to fuzzy/strength/recency when embeddings are off). Strength follows
`BaseStrength × e^(−DecayRate × daysSinceAccess)`, with importance reducing the effective decay rate.

**Conflict resolution on store.** New memories are compared against existing ones by semantic similarity:

| Similarity | Action |
|---|---|
| ≥ 0.95 (duplicate) | Reinforce the existing memory |
| ≥ 0.8 (supersede) | Archive the old memory, store the new one (history preserved) |
| ≥ 0.6 (coexist) | Store as a related memory |
| < 0.6 | Store independently |

**Code indexing pipeline.** Register a workspace → sub-projects auto-discovered by language → a watcher queues changed files → workers ingest (hash → compiler context → symbols → embed → store), then build the symbol-reference graph, then (optionally) generate LLM summaries. Searches and the dashboard read the resulting index.

---

## Configuration

Edit `agentic-memory/appsettings.json`. Models auto-download on first use. Key sections:

```json
{
  "Server":      { "Port": 3377, "BindAddress": "0.0.0.0" },
  "Embeddings":  { "Enabled": true,  "ModelDimensions": 384, "MaxSequenceLength": 256 },
  "Generation":  { "Enabled": false, "MaxNewTokens": 512, "Temperature": 0.7, "TopP": 0.9 },
  "CodeIndex":   { "Enabled": true,  "EnableCSharpRoslyn": true, "EnableTypeScriptV8": true,
                   "AutoDownloadTypeScript": true, "TypeScriptVersion": "5.5.4" },
  "Maintenance": { "Enabled": true,  "DecayIntervalHours": 24, "PruneThreshold": 0.1,
                   "ConsolidationEnabled": true, "SimilarityThreshold": 0.8 },
  "Conflict":    { "DuplicateSimilarityThreshold": 0.95,
                   "SupersedeSimilarityThreshold": 0.8,
                   "CoexistSimilarityThreshold": 0.6 }
}
```

| Setting | Default | Notes |
|---|---|---|
| `Server.Port` | `3377` | Override with `--port` / `-p`; bind with `--bind` / `-b` |
| `Embeddings.Enabled` | `true` | Disable to skip the embedding model entirely |
| `Generation.Enabled` | **`false`** | Opt-in; enabling triggers a ~5 GB model download |
| `CodeIndex.EnableTypeScriptV8` | `true` | Auto-downloads the TypeScript compiler |
| `Maintenance.PruneThreshold` | `0.1` | Memories below this strength are removed |
| `Storage.DatabasePath` | `./Data/agentic-memory.db` | Single embedded LiteDB file |

---

## Architecture

```
agentic-memory/
├── Brain/              # Memory engine
│   ├── Embeddings/     #   ONNX SBERT vector generation
│   ├── Search/         #   semantic + trigram fuzzy search
│   ├── Generation/     #   Phi-4-mini local LLM (ONNX GenAI)
│   ├── Conflict/       #   duplicate / supersede / coexist resolution
│   ├── Maintenance/    #   decay, consolidation, pruning, compaction
│   └── Storage/        #   LiteDB repository
├── CodeIndex/          # Code intelligence
│   ├── CSharp/         #   Roslyn provider + reference index
│   └── TypeScript/     #   TS compiler in V8 (ClearScript) + bridge.js
├── Tools/              # MCP tools (official ModelContextProtocol SDK)
├── Extensions/         # REST API routing & DI wiring
├── Persistence/        # Workspace / project state
├── Models/             # API & domain models
└── wwwroot/            # Built React dashboard (served statically)

react-dashboard/        # React + Vite + Tailwind source
```

**Tech stack:** .NET 10 · ASP.NET Core (Kestrel) · LiteDB · ONNX Runtime + ONNX Runtime GenAI · ML.Tokenizers · Roslyn (`Microsoft.CodeAnalysis.CSharp`) · ClearScript V8 · ModelContextProtocol .NET SDK · React 18 + Vite + TypeScript + Tailwind · Spectre.Console.

---

## Testing

```bash
dotnet test
```
xUnit v3 suite covering memory operations, MCP compliance, search accuracy, code indexing, and maintenance behaviors.

---

## License

MIT — use freely in personal and commercial projects.

---

<div align="center">

**Agentic Memory** · *Give your AI agents memory, code sense, and a brain of their own.*

[GitHub](https://github.com/lionheartaaron/agentic-memory) • [Issues](https://github.com/lionheartaaron/agentic-memory/issues)

</div>
