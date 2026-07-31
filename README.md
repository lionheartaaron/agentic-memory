<div align="center">

# Agentic Memory

### **A local-first context engine for AI agents: memory, code intelligence, and an on-device LLM**

[![Release](https://img.shields.io/github/v/release/lionheartaaron/agentic-memory?label=release&color=success)](https://github.com/lionheartaaron/agentic-memory/releases/latest)
[![CI](https://github.com/lionheartaaron/agentic-memory/actions/workflows/ci.yml/badge.svg)](https://github.com/lionheartaaron/agentic-memory/actions/workflows/ci.yml)
[![.NET 10](https://img.shields.io/badge/.NET-10.0-512BD4?style=flat&logo=dotnet)](https://dotnet.microsoft.com/)
[![MCP Compatible](https://img.shields.io/badge/MCP-Compatible-green?style=flat)](https://modelcontextprotocol.io/)
[![Local First](https://img.shields.io/badge/Local-First-blue?style=flat)]()
[![License: MIT](https://img.shields.io/badge/License-MIT-yellow.svg)](https://opensource.org/licenses/MIT)

*Persistent memory, compiler-grade code understanding, and a built-in generative model, all running on your machine and exposed to any MCP-compatible agent.*

[Quick Start](#quick-start) • [The Three Pillars](#the-three-pillars) • [Dashboard](#dashboard) • [Connect an Agent](#connect-an-agent) • [MCP Tools](#mcp-tools) • [Configuration](#configuration) • [Auth](#authentication) • [Your Data](#where-your-data-lives) • [Upgrades](#schema-versions-and-upgrades) • [Releasing](#releasing)

</div>

---

## What is this?

Agentic Memory started as a memory layer for AI assistants. It is now a **local AI context platform**: three subsystems, one embedded database, no cloud dependencies.

| | Pillar | What it gives your agent |
|---|---|---|
| 🧠 | **Memory Engine** | Persistent, semantically-searchable memory that strengthens with use and fades when forgotten |
| 🔍 | **Code Intelligence** | Compiler-backed understanding of C#, TypeScript and React codebases: symbols, references, dependency graphs, API surfaces |
| ✨ | **Local Generative AI** | An on-device LLM (Phi-4-mini) for code summaries and chat, with no API keys and no data leaving your machine |

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

    subgraph Engine["Local Engine, 100% offline"]
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

- **Hybrid retrieval:** ONNX SBERT embeddings (`all-MiniLM-L6-v2`, 384-dim), BM25F lexical ranking, trigram fuzzy matching, slot lookup and recency, fused by Reciprocal Rank Fusion and diversified with MMR.
- **Scoped, not one flat pool:** every read is bounded by user, and by which companion is asking. Some facts are shared by all of a user's companions; others are private to one.
- **Nothing is silently lost:** decay is a *ranking* signal only. Old memories are archived, superseded versions are kept as history, and a user-requested forget is tombstoned and restorable. Physical removal happens only on an explicit retention schedule.
- **Conflict resolution on write:** near-duplicates reinforce; a replacement is allowed only when the slot, subject, scope and provenance say it is legal; a genuine contradiction is raised rather than resolved automatically.
- **Bitemporal:** memories carry both when they were learned and when they were true, so `AsOf` can answer a question as the store would have answered it at a past instant.

### 🔍 Code Intelligence

Every query goes through **real compiler APIs** rather than regex or ctags, so answers reflect the whole program.

- **C# via Roslyn:** a whole-program `CSharpCompilation` resolves types, inheritance chains, and symbol identity. Extracts symbols, signatures, modifiers, attributes, XML docs, and an inverted reference index for fast find-all-references and call-graph attribution.
- **TypeScript and React via the TS compiler itself:** the real `typescript.js` LanguageService runs in-process inside a V8 engine (ClearScript). Whole-program type resolution across barrel re-exports, semantic diagnostics, symbols, and references.
- **Framework-aware "domain facts":** detects ASP.NET routes & DI, EF Core entities, MediatR handlers, React hooks, TanStack Query/Mutation chains, fetch() API clients, and navigation edges.
- **Dependency & dead-code analysis:** fan-in/fan-out metrics, hotspots, entry points, a full dependency graph, orphan (unused public symbol) detection, and test-to-subject linkage.
- **Incremental & background:** a file watcher ingests changes; dedicated workers handle indexing, reference-graph building, and summarization without blocking.

### ✨ Local Generative AI

An LLM that runs on your CPU, with no GPU or cloud required.

- **Model:** Microsoft `Phi-4-mini-instruct`, int4-quantized ONNX (128K context, ~5 GB), served via ONNX Runtime GenAI.
- **Uses:** generates concise file/symbol summaries that feed semantic search, and powers the dashboard chat (with streaming via Server-Sent Events).
- **Opt-in:** disabled by default; flip `Generation.Enabled` to `true` to auto-download and enable it. Everything else works without it.

---

## Quick Start

### Download a build

[The latest release](https://github.com/lionheartaaron/agentic-memory/releases/latest) covers
Windows, macOS and Linux on x64 and arm64. Every asset is self-contained, meaning the .NET runtime
is inside it, so nothing else needs installing.

| | Install it | Just run it |
|---|---|---|
| **Windows** | `.msi`, per-user, no admin prompt, Start Menu entry | `-portable.zip`: unpack and run, keeps everything in its own folder |
| **macOS** | `.dmg`, drag to Applications; launching it opens the dashboard | `.tar.gz` |
| **Linux** | `.deb`, puts `agentic-memory` on your `PATH` | `.AppImage` (x64) or `.tar.gz` |

The `.dmg` is not notarized, so the first launch needs right-click → Open. Model weights are not in
any of these; the embedding model (~90 MB) downloads on first run. See
[Releasing](RELEASING.md#what-a-release-produces) for the full asset list and what each one does
with your data.

Everything below is for building from source.

### Prerequisites
- **.NET 10 SDK**
- **Node.js + npm** (optional): the build compiles the React dashboard when npm is present

### 1. Clone & build
```bash
git clone https://github.com/lionheartaaron/agentic-memory.git
cd agentic-memory
dotnet build
```
> Building runs `npm install && npm run build` for the dashboard (via an MSBuild target) and emits it
> into `wwwroot/`. Without npm it is skipped and the previous build is used; `-p:SkipDashboardBuild=true`
> skips it deliberately.

**Platforms.** Windows, macOS and Linux, on x64 and arm64. Everything builds AnyCPU with no fixed
bitness, and both projects take that from a single `Directory.Build.props`, so the application and
its tests can never be compiled for different architectures.

ClearScript publishes V8 as one package per runtime identifier, so the build selects the one it
needs rather than pinning `win-x64`:

```bash
dotnet build                        # the host's own platform
dotnet publish -r linux-x64         # or osx-arm64, win-arm64, linux-arm64 …
```

Only the matching native is pulled in, because each is tens of megabytes and a sidecar ships for
one platform. On a runtime identifier ClearScript does not publish a V8 for, the build still
succeeds and says so: TypeScript code intelligence reports itself unavailable and everything else
works.

### 2. Run the server
```bash
dotnet run --project agentic-memory
```

### 3. You're live
```
── 🧠 Agentic Memory Server v1.2.0 (MCP SDK) ──────────────────────────

  🌐  Listening on     http://0.0.0.0:3377
  🔒  Auth             ⚠️  None: reachable from the whole network · set Server:ApiKey
  💾  Database         …\AgenticMemory\agentic-memory.db · schema v3
  📁  Data             C:\Users\you\AppData\Local\AgenticMemory · per-user default
  🗃  Models           …\bin\Debug\net10.0
  🔍  Embeddings       ✅ Active · all-MiniLM-L6-v2.onnx · 384-dim
  🤖  Generative       🚫 Disabled
  📚  Code index       ✅ C# Roslyn  ·  ✅ TypeScript V8
```
Dashboard at **http://localhost:3377**, MCP at **/mcp**. See [Authentication](#authentication) before
you expose it to anything.

The embedding model (~90 MB) downloads automatically on first run. The optional generative model (~5 GB) downloads only if you enable `Generation`.

---

## Dashboard

Open **http://localhost:3377** for a React + Vite + Tailwind UI that exposes the whole system:

- **Overview:** memory stats, strength distribution, system health
- **Memories:** search, browse, create/edit, point-in-time recall, and restore for anything archived or forgotten
- **Memory detail:** full event history, and how a structured fact changed over time
- **Conflicts:** contradictions awaiting a decision, both sides side by side, keep either or dismiss
- **Workspaces:** register a codebase, auto-discover sub-projects, watch indexing progress
- **Code Intelligence:** dependency graph, hotspots & entry points, symbol search, domain facts (endpoints, entities, etc.), per-file LLM summaries
- **Worker Status:** live indexing, summary, and reference-graph queues
- **Chat:** talk to the local Phi-4-mini model with streaming responses
- **Settings:** API key, schema version and migration history, snapshots, maintenance and reset

A selector in the sidebar chooses which user's memories every page shows. Memories are scoped per
user, and a request that names none answers for the user called `default`, so a store an agent
writes to under its own ID looks empty until this is set.

The dashboard is pre-built into `wwwroot/` and served as static files by the .NET server, so there
is no separate process to run. It is served without authentication because a browser cannot attach
a header to its own page load; when the server has a key set, the first API call fails and the
dashboard prompts for it.

---

## Connect an Agent

Any [Model Context Protocol](https://modelcontextprotocol.io/) client connects to:

```
http://localhost:3377/mcp
```

Streamable HTTP, protocol revision `2025-06-18`. If you have set `Server.ApiKey`, add the header to
your client's config. Every example below accepts a `headers` object:

```json
"headers": { "X-API-Key": "your-key" }
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

Drop something like this into your agent's system/instructions file (e.g. `.github/copilot-instructions.md`) so that it uses the memory:

```markdown
You have a persistent memory system via the `agentic-memory` MCP server.

- **Search memories** at the start of recurring topics, before making
  recommendations, or when asked about past decisions.
- **Store memories** when the user asks you to remember something, or when an
  important preference, decision, or technical pattern is established.
- **Always search before storing** to avoid duplicates; update instead of duplicating.
- Call `get_subproject_context` once per session, then use `get_file_context` /
  `get_symbol_context` / `search_code` to ground answers in the indexed codebase.
```
</details>

---

## MCP Tools

> If you are building an AI companion on top of this, **[companion-api.md](companion-api.md)**
> covers the whole surface end to end: the scoping model, the extraction pass that decides what to
> remember after each turn, real-time recall over MCP, the conflict loop, every payload, and the
> traps.

**Memory.** Every tool takes `user_id`, and `companion_id` where a memory can be private.

| Tool | Description |
|------|-------------|
| `search_memories` | Semantic + lexical + fuzzy search, scoped to a user and companion |
| `store_memory` | Create a memory; conflicts are detected and resolved on write |
| `update_memory` | Correct or extend an existing memory in place |
| `get_memory` | Fetch one memory in full, with provenance and history links |
| `forget_memory` | Mark as forgotten at the user's request: tombstoned, not destroyed, and restorable |
| `restore_memory` | Undo a forget, archive or supersede, bringing a memory back into recall |
| `get_memory_history` | Every recorded event for one memory |
| `get_slot_history` | How a structured fact changed over time, superseded versions included |
| `get_tag_history` | Same for a tag; prefer `get_slot_history` for structured facts |
| `list_conflicts` | Contradictions awaiting a decision, each with both memories in full so a side can be chosen from one call |
| `resolve_conflict` | Settle one once the user has clarified. The winner must be one of the two sides; the loser becomes history, never deleted |
| `list_slots` | The registered structured predicates |
| `get_stats` | Memory system statistics |

**Code intelligence.** All take an optional `subproject` to scope the lookup.

| Tool | Description |
|------|-------------|
| `get_subproject_context` | Workspace layout: sub-projects, entry points, manifests. Call first |
| `get_file_context` | A file's symbols, signatures, imports/exports and dependencies, without bodies |
| `get_symbol_context` | Definition, implementations, callers and reference count for a named symbol |
| `get_symbol_sourcecode` | Just that symbol's source, instead of reading the whole file |
| `search_code` | Semantic + keyword search when you don't yet know the file or symbol |
| `list_symbols` | Symbols in a file or sub-project |
| `get_callers` | Everything that calls a given symbol |

---

## REST API

The dashboard runs entirely on a documented HTTP API, which is also what you would build a custom
integration against. Highlights:

| Area | Endpoints |
|------|-----------|
| **Memory** | `GET/POST /api/memory`, `GET/PUT/DELETE /api/memory/{id}`, `POST /api/memory/search`, `POST /api/memory/{id}/restore`, `GET /api/memory/{id}/history` |
| **Slots & conflicts** | `GET /api/memory/{slots,slot,conflicts}`, `GET /api/memory/conflicts/{id}`, `POST /api/memory/conflicts/{id}/resolve` |
| **Workspaces** | `GET/POST /api/workspaces`, `POST /api/workspaces/{id}/discover`, `.../activate`, `.../reindex`, `GET /api/workspaces/{id}/{sub-projects,files,stale-files,error-files}` |
| **Code Intelligence** | `GET /api/workspaces/{id}/intelligence/{symbols,hotspots,entrypoints,graph,domain-facts,overview,semantic,manifests,file/{fileId}}` |
| **Generation** | `POST /api/generate`, `POST /api/generate/stream` (SSE), `GET /api/generate/status` |
| **Files** | `GET /api/files/browse`, `GET /api/file/context`, `POST /api/file/summary` |
| **Key-value** | `GET/PUT/DELETE /api/kv/{key}` |
| **Admin** | `GET /api/admin/{status,stats,health,paths,database,backups,maintenance-stats,users}`, `POST /api/admin/backups`, `DELETE /api/admin/{memories,code-index,workspaces}`, `POST /api/admin/full-reset` |

`DELETE /api/memory/{id}` marks a memory forgotten and tombstones it; it does not destroy it. Use
`POST /api/memory/{id}/restore` to undo. Physical removal happens later on a retention schedule.

<details>
<summary><b>Example: store and search memories via curl</b></summary>

```bash
# Store. title and summary are required; omitting userId means the "default" user.
curl -X POST http://localhost:3377/api/memory \
  -H "Content-Type: application/json" \
  -d '{
    "title": "User prefers dark themes",
    "summary": "Always suggest dark mode configurations",
    "content": "The user prefers dark themes across IDEs, terminals, and web apps.",
    "tags": ["user-preference", "ui"],
    "importance": 0.8,
    "userId": "alex"
  }'

# Store something only one companion knows
curl -X POST http://localhost:3377/api/memory \
  -H "Content-Type: application/json" \
  -d '{
    "title": "Surprise party on the 14th",
    "summary": "Do not mention this to anyone else",
    "userId": "alex", "companionId": "aria", "visibility": "Scoped"
  }'

# Search, as a particular companion
curl -X POST http://localhost:3377/api/memory/search \
  -H "Content-Type: application/json" \
  -d '{ "query": "ui preferences", "topN": 5, "userId": "alex", "companionId": "aria" }'
```
</details>

---

## How It Works

**Memory scoring.** Retrieval runs five independent channels: vector, lexical (BM25F), slot,
recency and link. They are combined with **Reciprocal Rank Fusion**, then diversified with MMR. RRF
consumes each channel's *ranking* rather than its score, because a cosine and a BM25 value are not
on the same scale, and summing them weighted lets whichever channel happens to produce larger
numbers dominate. Channel weights live under `Retrieval` in `appsettings.json`. Strength follows
`BaseStrength × e^(−DecayRate × daysSinceAccess)` and is a ranking signal only; it never decides
whether a memory is kept.

**Conflict resolution on store.** Similarity only *proposes* candidates. Whether one memory may
replace another is decided by the `SupersedeGate`, deterministically, from the slot, subject, scope
and provenance:

| Outcome | When |
|---|---|
| **Duplicate** | Same slot, same value → reinforce the existing memory rather than store a copy |
| **Supersede** | Same slot and subject, different value, and replacement is legal → the old version is retained as history |
| **Conflict** | A real contradiction, or an attempt to overwrite immutable persona → raised for the user, never resolved automatically |
| **Coexist** | Different subject, different user, or not a contradiction at all → both stay active |

A threshold alone cannot tell "contradicts" from "same topic": *"I love pizza"* and *"I love pasta"*
sit well above any similarity bar, so the old rule deleted one food preference when you stored
another. Nothing here is ever hard-deleted by conflict resolution.

**Settling one.** A raised conflict waits for a person. Both `list_conflicts` over MCP and
`GET /api/memory/conflicts` return the contradiction with **both memories inlined**: title, value,
state and provenance. A `UserStated` side against a `CompanionInferred` one is rarely a real
toss-up, and having that in the same response is what makes the decision answerable in one call.

Resolving takes the ID of the side that is correct, and it has to be one of those two. Any other ID
is refused: the losing side is worked out as "whichever one is not the winner", so an unrelated ID
would have superseded the new memory in favour of something that was never in the conflict.
Settling twice is refused for the same shape of reason, since it would supersede the first winner as
well and leave the slot with nothing current. The loser is superseded, never deleted, and
`restore_memory` brings it back.

**Code indexing pipeline.** Register a workspace → sub-projects auto-discovered by language → a watcher queues changed files → workers ingest (hash → compiler context → symbols → embed → store), then build the symbol-reference graph, then (optionally) generate LLM summaries. Searches and the dashboard read the resulting index.

---

## Configuration

Edit `agentic-memory/appsettings.json`. Models auto-download on first use. Key sections:

```json
{
  "Server":      { "Port": 3377, "BindAddress": "0.0.0.0",
                   "ApiKey": "", "ApiKeyHeader": "X-API-Key" },
  "Storage":     { "DataDirectory": "", "ModelsDirectory": "", "DatabasePath": "" },
  "Embeddings":  { "Enabled": true,  "ModelDimensions": 384, "MaxSequenceLength": 256 },
  "Generation":  { "Enabled": false, "MaxNewTokens": 512, "Temperature": 0.7, "TopP": 0.9 },
  "CodeIndex":   { "Enabled": true,  "EnableCSharpRoslyn": true, "EnableTypeScriptV8": true,
                   "AutoDownloadTypeScript": true, "TypeScriptVersion": "5.5.4" },
  "Maintenance": { "Enabled": true,  "DecayIntervalHours": 24,
                   "ArchiveEpisodicAfterDays": 180, "PurgeForgottenAfterDays": 30,
                   "ConsolidationEnabled": true, "SimilarityThreshold": 0.9,
                   "BackupBeforeDestructiveOperations": true, "BackupRetentionCount": 10 },
  "Conflict":    { "DuplicateSimilarityThreshold": 0.92,
                   "CandidateSimilarityThreshold": 0.55,
                   "MaxCandidates": 25, "AutoSupersedeEnabled": true },
  "Retrieval":   { "VectorChannelWeight": 1.0, "LexicalChannelWeight": 1.0,
                   "SlotChannelWeight": 1.25, "RecencyChannelWeight": 0.4,
                   "LinkChannelWeight": 0.3, "DiversityLambda": 0.75 }
}
```

| Setting | Default | Notes |
|---|---|---|
| `Server.Port` | `3377` | Override with `--port` / `-p`; bind with `--bind` / `-b` |
| `Server.ApiKey` | *(empty)* | **Empty means no authentication.** See [Authentication](#authentication) |
| `Embeddings.Enabled` | `true` | Disable to skip the embedding model entirely |
| `Generation.Enabled` | **`false`** | Opt-in; enabling triggers a ~5 GB model download |
| `CodeIndex.EnableTypeScriptV8` | `true` | Auto-downloads the TypeScript compiler |
| `Maintenance.ArchiveEpisodicAfterDays` | `180` | Old episodic memories are **archived**, never deleted |
| `Maintenance.PurgeForgottenAfterDays` | `30` | The only path to physical removal, and only for user-requested forgets |
| `Conflict.CandidateSimilarityThreshold` | `0.55` | Proposes candidates only; the `SupersedeGate` decides |
| `Storage.DatabasePath` | *(empty)* | Single embedded LiteDB file; empty means the data folder below |

`Retrieval` values are measured against the eval harness, and `ConfigurationDriftTests` fails the
build if the shipped file drifts from what that harness assumes.

---

## Authentication

**Off by default.** The server binds `0.0.0.0:3377`, so out of the box every machine on your network
can read and write your memories with an unauthenticated `GET`. That is acceptable on a machine you
control and not anywhere else, and the startup banner says so.

Set a key and every `/api` and `/mcp` request needs it:

```json
{ "Server": { "ApiKey": "a-long-random-string", "ApiKeyHeader": "X-API-Key" } }
```

```bash
curl -H "X-API-Key: a-long-random-string" localhost:3377/api/memory
curl -H "Authorization: Bearer a-long-random-string" localhost:3377/api/memory   # also accepted
```

`Authorization: Bearer` works too, because plenty of HTTP and MCP clients can send that and cannot
send an arbitrary custom header.

| | Behaviour |
|---|---|
| **Protected** | Everything under `/api` and `/mcp`: all memory data, admin, generation, and the whole MCP surface |
| **Open** | `GET /api/admin/health`, so a host process can wait for startup without holding the key |
| **Open** | The dashboard's static assets, since a browser cannot attach a header to its own page load. The data they request is protected. |

The dashboard reads its key from `localStorage.agenticMemoryApiKey` and sends it on every call; a
`401` surfaces as a distinct `UnauthorizedError` rather than a generic failure.

**Prefer the environment variable** for a packaged app:

```bash
AGENTIC_MEMORY_API_KEY=... agentic-memory
```

It overrides the config file, so a host that generates a key per install never has to write one.
There is deliberately **no command-line flag**, because process arguments are readable by any other
process on Windows, Linux and macOS alike, so a secret passed that way is not a secret. For the same
reason the shipped `appsettings.json` never carries a key, and a build-failing test enforces it: a
key committed to the repository is identical on every install, so recovering it once unlocks
everybody.

Comparison is fixed-time. A naive string equality returns as soon as two bytes differ, which over
many requests leaks the key one byte at a time.

> This is a single shared secret rather than user accounts, which is the right shape for one local
> process serving one person. It does not encrypt anything; use loopback binding as well.

---

## Where your data lives

The database goes in the **per-user data folder**. Model weights stay **beside the binary**.

| | Location | Why |
|---|---|---|
| Database, snapshots | `%LOCALAPPDATA%\AgenticMemory` · `~/Library/Application Support/AgenticMemory` · `$XDG_DATA_HOME/agentic-memory` | Per user, and it has to survive an application update |
| Model weights | Next to the executable (`Models/…`) | Shipped with the build, identical for every user, re-supplied by the next version |

This split matters when the server runs as a sidecar inside a desktop app. That program directory is
read-only in a signed macOS bundle, needs elevation under `%ProgramFiles%`, and **is replaced
wholesale on every auto-update**. The last one destroys data: a database kept there is deleted the
first time the app updates itself.

An existing install is migrated on first run: the database and its write-ahead log move together as
one unit, and the move is reported once on the console. If both locations somehow hold a database,
neither is touched and both paths are named. Two databases are two histories, and picking one
silently is worse than asking.

```bash
agentic-memory --data-dir "/path/to/userData"     # e.g. Electron's app.getPath('userData')
agentic-memory --models-dir "/path/to/models"     # only if the install location is read-only
```

Equivalently `AGENTIC_MEMORY_DATA_DIR` / `AGENTIC_MEMORY_MODELS_DIR`, or `Storage:DataDirectory` /
`Storage:ModelsDirectory` in `appsettings.json`, in that order of precedence. Dropping a
`portable.txt` file beside the executable keeps everything in `./Data` instead, which is useful for
a checkout or a USB stick.

`GET /api/admin/paths` reports every resolved location, and they are printed at startup.

An optional `appsettings.json` in the data folder is layered over the bundled one, which is how a
packaged app gets editable configuration when its own copy is inside a read-only bundle.

### Running from a copied folder

A release archive is self-contained, so an installer can copy the folder anywhere and run the binary
in it. Two things decide whether that install behaves:

| | |
|---|---|
| **Configuration** | Edit `appsettings.json` beside the binary after copying. An `appsettings.json` in the data folder is layered on top of it, so a read-only install can still be reconfigured later. |
| **Weights** | They are resolved against the models directory, which defaults to the folder the binary sits in: `Models/Embedding/` for the embedding model and its vocab, `Models/TypeScript/typescript.js` for the TypeScript compiler. Pre-seed those two and first run needs no network. `Models/Generative/…` is only read when `Generation.Enabled` is `true`. |

Set `Storage.DataDirectory` (or pass `--data-dir`) to the host's own user-data path so the database
lives outside the folder being copied, since that folder is what an update replaces. If the install
location is read-only, point `--models-dir` at a writable path as well, otherwise a model that has
not been pre-seeded cannot be written when it downloads.

---

## Schema versions and upgrades

The database records its own **schema version**, separately from the application version. They
answer different questions and move at different rates: a bug-fix release must not imply that the
stored data changed shape, and a schema change must not have to wait for a release boundary.

When the database is opened:

- **Older than this build** → it is migrated automatically, no user action. A snapshot is taken
  first, each step commits with its own version stamp in a single transaction, and every step is
  recorded in the file with the app version that ran it.
- **Same version** → nothing happens beyond noting which build opened it.
- **Newer than this build** → **the server refuses to start.** Old code against a new schema does
  not degrade gracefully. It destroys silently: reading fields it does not understand, dropping
  them on the next write, and leaving a file no version can make sense of. For an app that
  auto-updates, where a user can roll back or a stale sidecar can be launched beside a current one,
  that is a routine accident. So it exits non-zero with the reason on the console rather than
  coming up and quietly serving the wrong answers.

A step that fails leaves the file complete at the previous version rather than stranded part-way,
so the next launch resumes from there instead of starting over.

```bash
curl localhost:3377/api/admin/database
```

```json
{
  "schemaVersion": 3, "supportedSchemaVersion": 3, "appVersion": "1.1.0",
  "migratedOnThisStart": true, "migratedFromVersion": 2,
  "snapshotPath": "…/backups/20260731-001631-schema-v2-to-v3.db",
  "history": [{ "fromVersion": 2, "toVersion": 3, "name": "projects-to-workspaces", "appVersion": "1.1.0" }]
}
```

A host deciding whether it is safe to launch an older sidecar should compare `schemaVersion` against
that build's `supportedSchemaVersion`, not the app versions, which carry no such guarantee.

### Adding a migration

Write a step, give it the next version number, append it to `DatabaseSchema.Steps`. That is the
whole procedure: `Current` is derived from the list, so there is no second place to bump.

```csharp
public sealed class MyStep : IMigrationStep
{
    public int    Version => 4;
    public string Name    => "my-change";
    public int Apply(MigrationContext context) { /* … */ return documentsTouched; }
}
```

Two rules, because a step runs once on a user's only copy of their data:

1. **A shipped step is frozen.** Editing one after release means two databases claim the same
   version with different contents, which nothing downstream can detect. Fix a bad step with a new
   step at the next version.
2. **Prefer raw `BsonDocument` over current model types.** Those evolve; a step has to keep meaning
   what it meant on the day it shipped.

---

## Architecture

```
agentic-memory/
├── Brain/              # Memory engine
│   ├── Embeddings/     #   ONNX SBERT vector generation
│   ├── Search/         #   BM25F, trigram fuzzy, SIMD cosine
│   ├── Retrieval/      #   rank fusion, MMR, vector & lexical caches
│   ├── Generation/     #   Phi-4-mini local LLM (ONNX GenAI)
│   ├── Conflict/       #   supersede gate, polarity detection
│   ├── Slots/          #   structured predicates that drive conflict handling
│   ├── Maintenance/    #   decay, consolidation, archival, retention purge
│   └── Storage/        #   LiteDB repositories, event log, snapshots
├── CodeIndex/          # Code intelligence
│   ├── CSharp/         #   Roslyn provider + reference index
│   └── TypeScript/     #   TS compiler in V8 (ClearScript) + bridge.js
├── Persistence/        # The shared LiteDB connection
│   └── Migrations/     #   schema version stamp, migration runner, steps
├── Configuration/      # Settings, path resolution, data-folder migration
├── Tools/              # MCP tools (official ModelContextProtocol SDK 2.0)
├── Extensions/         # REST API routing & DI wiring
├── Middleware/         # Optional API-key authentication
├── Helpers/            # Startup banner and console reporting
├── Logging/            # Spectre.Console logger provider
├── Models/             # API & domain models
└── wwwroot/            # Built React dashboard (served statically)

react-dashboard/        # React + Vite + Tailwind source
agentic-memory-tests/   # xUnit v3: memory, code index, MCP, platform, persistence
```

**Tech stack:** .NET 10 · ASP.NET Core (Kestrel) · LiteDB 5 · ONNX Runtime 1.28 + ONNX Runtime GenAI 0.15 · ML.Tokenizers · Roslyn 5.6 (`Microsoft.CodeAnalysis.CSharp`) · ClearScript V8 7.5 · ModelContextProtocol .NET SDK 2.0 (MCP `2025-06-18`) · React 18 + Vite + TypeScript + Tailwind · Spectre.Console 0.57.

---

## Testing

```bash
dotnet test
```

**519 tests**, xUnit v3. Alongside the usual unit coverage there are five suites that exist to fail
the build rather than to describe behaviour:

- **Retrieval quality:** 20 gold `query → memory` pairs deliberately written to share no vocabulary
  with their target, against 200 distractors, with `recall@1/5/20` and MRR as hard floors.
- **Configuration drift:** the shipped `appsettings.json` must agree with the defaults the eval
  harness measures, and must never pin state to the program directory.
- **Migration:** fresh stamp, upgrade path, adoption of the previous versioning scheme, refusal of
  a newer database, rollback, and resume-after-failure.
- **Authentication:** that a configured key is required on every data path including MCP, that
  health and the static dashboard stay reachable without one, and that a rejection never echoes the
  key back.
- **Conflict resolution:** that a winner belonging to neither side changes nothing, that resolving
  without a choice changes nothing, that settling twice is refused, and that a forgotten side is
  still shown in the decision it is part of.

---

## Releasing

Every push and pull request runs the full suite on Windows, Linux and macOS, plus a dashboard build
([`ci.yml`](.github/workflows/ci.yml)). Work lands on `develop`; `main` is always the last released
state.

A release is a tag pushed on `main`. [`release.yml`](.github/workflows/release.yml) then builds the
server for all six targets it runs on and publishes them as a GitHub Release:

| Platform | Targets | Format |
|---|---|---|
| Windows | `win-x64`, `win-arm64` | `.zip` |
| Linux | `linux-x64`, `linux-arm64` | `.tar.gz` |
| macOS | `osx-arm64`, `osx-x64` | `.tar.gz`, ad-hoc signed |

Each one is self-contained, with the .NET runtime inside it, so the host machine needs nothing
installed. Each comes with `SHA256SUMS.txt`, which matters because a parent process that downloads
this sidecar is going to execute it. Model weights are not in the archives; they download on first
use.

Before packaging, every target the runner can execute is started and has to answer
`/api/admin/health` **and** serve the dashboard at `/`. That is the only step that exercises the
assembled artifact: natives loading, the schema migrator creating a database from nothing, Kestrel
serving. A green test suite says nothing about any of it.

**[RELEASING.md](RELEASING.md)** is the checklist: branches, the version bump (enforced against the
tag, because it gets written into user databases), how the app version and the database schema
version differ, and hotfixes.

---

## License

MIT. Use freely in personal and commercial projects.

---

<div align="center">

**Agentic Memory** · *Give your AI agents memory, code sense, and a brain of their own.*

[GitHub](https://github.com/lionheartaaron/agentic-memory) • [Issues](https://github.com/lionheartaaron/agentic-memory/issues)

</div>
