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
- ORM choice (Drizzle alternative)

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

### Phase 3: Core Runtime — 🚧 IN PROGRESS (Mostly Done)
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
Ported:
- Built-in tools (ReadTool, WriteTool, EditTool, BashTool, GlobTool, GrepTool, WebFetchTool) in `Tools/` folder
Remaining:
- LLM provider integration (actual provider clients)
- Credentials system
- Model/Provider resolution service
- Session history, compaction, revert
- System context, skills, references
- Git integration
- Snapshot system
- Depends on: Schema, Protocol

### Phase 4: Data Layer — PENDING
Port SQLite data access (Drizzle → ADO.NET / Dapper / EF Core).
- Depends on: Schema

### Phase 5: Server — PENDING
Port ASP.NET Core Minimal APIs (Hono → Minimal APIs).
- Depends on: Core, Protocol, Data

### Phase 6: Client — PENDING
Port HTTP client.
- Depends on: Protocol

### Phase 7: Sdk — PENDING
Port embedded host.
- Depends on: Client, Core, Server

### Phase 8: Cli / Tui / Plugin — PENDING
Port CLI entry point, TUI, and plugin system.
- Depends on: Sdk

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
