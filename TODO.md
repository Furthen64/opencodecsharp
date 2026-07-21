# OpenCode C# Port - Progress Tracker

## Objective
Port the OpenCode TypeScript monorepo (~32 packages, Effect TS/Drizzle/Hono/SolidJS) to a .NET 10 C# solution in `opencode_cs` folder.

## Architecture
- Original TS code moved to `opencode_ts`
- New .NET code in `opencode_cs`
- .NET 10 SDK (10.0.110)
- Layered dependency: Schema ← Protocol ← Core/Data ← Server/Client ← Sdk ← Cli/Tui/Plugin

## Key Decisions (Still Open)
- Effect TS replacement strategy (what to use for functional error handling / effect system)
- UI port vs keep JS (SolidJS parts)
- TUI framework choice
- ~~ORM choice (Drizzle alternative)~~ → Dapper + Microsoft.Data.Sqlite

## Naming Convention
- TS uses nested namespaces (`Schema.Agent.Info`), C# uses flat naming (`AgentInfo`)
- Schema types are C# records with `[JsonDerivedType]` for discriminated unions

---

## Phase Status

### Phase 1: Schema — ✅ COMPLETE
All schema definitions ported as C# records (~20 files).

### Phase 2: Protocol — ✅ COMPLETE
All protocol definitions ported as C# records (~18 files).
- Added missing schema types: `PermissionSaved.cs`, `PtyTicket.cs`, `ProjectCopy.cs`, `Workspace.cs`
- Fixed namespace naming to align with flat C# conventions

### Phase 3: Core Runtime — ✅ COMPLETE
Ported foundations:
- CoreSchema: path/int helpers, model ref parsing
- GlobalPaths: XDG-compliant path resolution
- FsUtil: filesystem utilities (read, write, glob, find-up, mime detection)
- Config system: ConfigInfo, ConfigDocument, JSON/JSONC parsing with comment stripping
- Tool framework: Tool base class, ToolDefinition, ToolRegistry, ToolContext, ToolFailure
- State management: StateManager with transform/reload pattern
- AgentService: agent selection, resolution, default handling
- ProjectService: project resolution
- LocationService: location info
- Session runtime: SessionStore, SessionExecution, error types, input records
- Event system: EventService with pub/sub, durable events, serialized events
- Permission system: PermissionService with evaluate/ask/assert/reply, wildcard matching
- Session runner: SessionRunner with LLM streaming, tool execution loop, step bounds
- Built-in tools (ReadTool, WriteTool, EditTool, BashTool, GlobTool, GrepTool, WebFetchTool) in `Tools/` folder
- SystemContext: Key/Source/Snapshot/Generation, SystemContextService (initialize/reconcile/replace), SystemContextRegistry, SystemContextBuiltins (env + date sources)
- InstructionContext: AGENTS.md discovery and system context source
- ModelService: model ref parsing (`provider/model` format)
- TokenEstimator: lightweight token count estimation
- GitService: repository discovery, clone, create, remote/history, tree operations, patch capture/apply
- SnapshotService: capture, files, diff, preview, restore, checkout (wraps GitService)
- CredentialService: interface + InMemoryCredentialService
- SkillService: skill source management, availability filtering
- ReferenceService: reference source management with state transform pattern
- Fixed Schema: added `[JsonDerivedType]` attributes to `SkillSource`

New in this session:
- **Tools**: ApplyPatchTool (patch parser, FileMutation, LocationMutation), QuestionTool, SkillTool, TodoWriteTool, WebSearchTool
- **Services**: PatchParser (Begin/End format with Add/Delete/Update hunks), FileMutationService (create/write/writeIfUnchanged/remove with conditional writes), LocationMutationService (path resolution with internal/external boundary), QuestionService (ask/reply/reject with pending map), SessionTodoService (in-memory todo store)
- **Session**: SessionCompactionService (auto-compaction with LLM summarization), SessionRevertService (stage/clear/commit with snapshots), SessionMessageUpdater (event reducer translating 20+ session events to message state)
- **LLM**: AISDKService (two-phase hook system for SDK/language model creation), ProviderRegistry (plugin registration)

New in this session (provider plugins + data):
- **AI Abstractions**: ILanguageModel interface, SerializerDefaults, LanguageModelConfig
- **Provider Plugins**: OpenAIProviderPlugin (SSE streaming chat completions), AnthropicProviderPlugin (Messages API with thinking/tool streaming), GoogleProviderPlugin (Generative AI streaming), OpenAICompatibleProviderPlugin (generic fallback)
- **Language Models**: OpenAILanguageModel, AnthropicLanguageModel, GoogleLanguageModel, OpenAICompatibleLanguageModel (all with full SSE streaming, tool call parsing, usage tracking)
- **LLM Bridge**: ModelLLMClient (ILLMClient impl bridging AISDKService to SessionRunner)
- **Data Layer**: Database (WAL-mode SQLite with migration runner), Schema (13 tables + indexes DDL), ProjectRepository, SessionRepository, MessageRepository, EventRepository, CredentialRepository, PermissionRepository

### Phase 4: Data Layer — ✅ COMPLETE
SQLite data access ported (Drizzle → Microsoft.Data.Sqlite + Dapper).
- 13 tables: project, session, message, session_message, session_input, session_context_epoch, todo, event_sequence, event, credential, permission, session_share, migration
- Full CRUD repositories for all entities
- WAL-mode SQLite with auto-migration on startup
- Depends on: Schema

### Phase 5: Server — IN PROGRESS
Port ASP.NET Core Minimal APIs (Hono → Minimal APIs).
- Depends on: Core, Protocol, Data
- Implemented: health/path, agent/skill/VCS/question, session lifecycle and status APIs
- Implemented: conversational session prompt and message-history APIs
- Implemented: SSE event endpoint, provider registration, model selection, streamed assistant replies, and built-in filesystem/shell tool execution
- Remaining: broader route parity, pagination/cursor semantics, durable repository-backed session/event storage, tool-result conversation state/continuations, and API integration tests

### Phase 6: Client — IN PROGRESS
Port HTTP client.
- Depends on: Protocol
- Implemented: minimal HTTP client for server health, session creation, prompt submission, and message history

### Phase 7: Sdk — PENDING
Port embedded host.
- Depends on: Client, Core, Server

### Phase 8: Cli / Tui / Plugin — IN PROGRESS
Port CLI entry point, TUI, and plugin system.
- Depends on: Sdk
- Implemented: minimal interactive terminal chat TUI backed by the HTTP client
- Remaining: full-screen terminal UX, live SSE rendering, commands, CLI entry point, and plugin integration

---

## Solution Structure
```
opencode_cs/
├── OpenCode.slnx
├── src/
│   ├── OpenCode.Schema/      # Pure domain records (zero deps)
│   ├── OpenCode.Protocol/    # API DTOs (→ Schema)
│   ├── OpenCode.Core/        # Session runtime, tools, LLM (→ Schema, Protocol)
│   ├── OpenCode.Data/        # SQLite data access (→ Schema)
│   ├── OpenCode.Server/      # ASP.NET Core Minimal APIs (→ Core, Protocol, Data)
│   ├── OpenCode.Client/      # HTTP client (→ Protocol)
│   ├── OpenCode.Sdk/         # Embedded host (→ Client, Core, Server)
│   ├── OpenCode.Cli/         # CLI entry point (→ Sdk)
│   ├── OpenCode.Tui/         # TUI components (→ Sdk)
│   └── OpenCode.Plugin/      # Plugin system (→ Core)
└── test/
    └── OpenCode.Tests/
```

## Source Reference
- TS source: `opencode_ts/packages/`
- Schema source: `opencode_ts/packages/schema/`
- Protocol source: `opencode_ts/packages/protocol/`
- Core source: `opencode_ts/packages/core/`

## Build Command
```bash
dotnet build opencode_cs/OpenCode.slnx
```
