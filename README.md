# Agentic Memory

**Agentic Memory** is a lightweight, self-contained memory and semantic search engine designed for AI and LLM agents. It provides fast, local, and human-like memory storage, retrieval, and maintenance, making it ideal for agentic workflows, chatbots, and autonomous systems.
Currently WIP and in early stages. This will be warning will removed when it is usable.

## Features

- **Semantic Search**: Uses local ONNX SBERT embeddings for high-quality vector search.
- **Fuzzy Text Search**: Trigram-based matching for typo tolerance and partial matches.
- **Memory Reinforcement & Decay**: Memories strengthen with use and decay over time, mimicking human memory.
- **Consolidation & Pruning**: Automatic merging of similar memories and removal of weak/unused ones.
- **Tagging & Metadata**: Memories can be tagged and linked for context and graph traversal.
- **Self-Contained**: No external dependencies—runs entirely offline using LiteDB and ONNX Runtime.
- **Fast TCP/HTTP API**: Minimal, high-performance server for agent integration.

## Use Cases

- LLM/AI agent memory backend (RAG, chatbots, assistants)
- Knowledge base for autonomous systems
- Local semantic search for documents or notes
- Prototyping agentic workflows with persistent, evolving memory

## How It Works

1. **Storage**: Memories are stored as documents in an embedded LiteDB database, including text, tags, embeddings, and metadata.
2. **Embeddings**: When enabled, the service uses a local ONNX SBERT model to generate vector embeddings for semantic search.
3. **Search**: Queries are scored using a combination of semantic similarity, fuzzy text match, memory strength, and recency.
4. **Reinforcement & Decay**: Accessing a memory reinforces it; unused memories decay and can be pruned automatically.
5. **Maintenance**: Background tasks consolidate similar memories and keep the database optimized.

## Quick Start

1. **Clone the repository**
```sh
git clone https://github.com/lionheartaaron/agentic-memory.git
cd agentic-memory
```

2. **Build and run**
```sh
dotnet build
dotnet run --project agentic-memory
```

3. **Configuration**
   - Edit `appsettings.json` to adjust server port, embedding model path, and maintenance options.
   - By default, the ONNX model and vocab will auto-download on first run.

4. **API Endpoints**
   - `GET /` — Human interactable welcome and search page
   - `GET /search?q=your+query` — Search memories (HTML or JSON)
   - `POST /api/memory` — Create a new memory node
   - `GET /api/memory/{id}` — Retrieve a memory node
   - `PUT /api/memory/{id}` — Update a memory node
   - `DELETE /api/memory/{id}` — Delete a memory node
   - `POST /api/memory/search` — Semantic search (JSON)
   - `GET /api/admin/stats` — Server statistics
   - ...and more (see server console output for full list)

5. **Default Interface**
   - Open your browser to `http://localhost:3377` to access the web interface for searching and managing memories.

## Example Usage

### Store a Memory

**Request:**
```sh
curl -X POST http://localhost:3377/api/memory \
  -H "Content-Type: application/json" \
  -d '{
    "title": "Learned about LiteDB limitations",
    "summary": "Discovered that LiteDB LINQ Any/All has restrictions.",
    "content": "When using LiteDB, avoid using .Any() with closures over local variables in queries.",
    "tags": ["dotnet", "litedb", "memory", "search"]
  }'
```

**Response:**
```json
{
  "id": "e3b0c442-98fc-1c14-9afb-4c4b6d6d7e7a",
  "title": "Learned about LiteDB limitations",
  "summary": "Discovered that LiteDB LINQ Any/All has restrictions.",
  "content": "When using LiteDB, avoid using .Any() with closures over local variables in queries.",
  "tags": ["dotnet", "litedb", "memory", "search"],
  "createdAt": "2024-06-07T12:34:56Z",
  "lastAccessedAt": "2024-06-07T12:34:56Z",
  "reinforcementScore": 1.0,
  "linkedNodeIds": []
}
```

### Search for Memories

**Request:**
```sh
curl "http://localhost:3377/search?q=LiteDB"
```

**Response:**  
- Returns a list of matching memories in JSON or HTML (depending on Accept header).

---

## Requirements

- .NET 10.0 or later
- No external database or cloud dependencies
- (Optional) Internet access for first-time model download

## Project Structure

- `Brain/` — Core memory logic, embeddings, search, and maintenance
- `Http/` — TCP/HTTP server, routing, handlers, and middleware
- `Configuration/` — App and server settings

## Why Agentic Memory?

- **Human-like**: Models memory decay, reinforcement, and consolidation.
- **Offline-first**: No cloud APIs or vendor lock-in.
- **Agent-optimized**: Designed for LLM and AI agent workflows.
- **Extensible**: Swap out storage, embeddings, or add new endpoints easily.

## License

MIT License

---

**Agentic Memory** — Fast, local, and human-like memory for your AI agents.
