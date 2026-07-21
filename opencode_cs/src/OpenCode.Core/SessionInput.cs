using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace OpenCode.Core;

public enum SessionInputDelivery
{
    Steer,
    Queue
}

public record SessionPrompt(
    string? Text,
    List<string>? Agents,
    List<PromptFile>? Files
);

public record PromptFile(
    string Uri,
    string? Name,
    string? Mime
);

public record SessionCreateInput(
    string? Id,
    string? Agent,
    Schema.ModelRef? Model,
    LocationRef Location
);

public record SessionUpdateInput(
    string SessionId,
    string? Title,
    long? Archived
);

public record SessionCompactInput(
    string SessionId,
    Schema.ModelRef Model,
    bool Auto
);

public record SessionListInput(
    string? WorkspaceId,
    string? Search,
    int? Limit,
    string? Order,
    SessionListAnchor? Anchor,
    string? Directory,
    string? Project,
    string? Subpath
);

public record SessionMessagesInput(
    string SessionId,
    int? Limit,
    string? Order,
    SessionMessageCursor? Cursor
);

public record SessionMessageCursor(
    string Id,
    string Direction
);

public record SessionPromptInput(
    string? Id,
    string SessionId,
    PromptInput Prompt,
    SessionInputDelivery? Delivery,
    bool? Resume
);

public record SessionSwitchAgentInput(
    string SessionId,
    string Agent
);

public record SessionSwitchModelInput(
    string SessionId,
    Schema.ModelRef Model
);

public record SessionRevertStageInput(
    string SessionId,
    string MessageId,
    bool? Files
);
