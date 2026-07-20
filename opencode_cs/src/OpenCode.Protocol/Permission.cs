namespace OpenCode.Protocol;

public record PermissionRequestListResponse(
    List<PermissionRequest> Data
);

public record PermissionSavedListResponse(
    List<PermissionSaved.Info> Data
);

public record SessionPermissionCreateRequest(
    string? Id,
    string Action,
    string[] Resources,
    bool Save,
    PermissionSource Metadata,
    PermissionSource Source,
    string? Agent
);

public record SessionPermissionCreateResponse(
    PermissionCreateResult Data
);

public record PermissionCreateResult(
    string Id,
    PermissionEffect Effect
);

public record SessionPermissionListResponse(
    List<PermissionRequest> Data
);

public record SessionPermissionGetResponse(
    PermissionRequest Data
);

public record SessionPermissionReplyRequest(
    PermissionReply Reply,
    string? Message
);
