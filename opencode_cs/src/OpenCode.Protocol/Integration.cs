namespace OpenCode.Protocol;

public record IntegrationListResponse(
    List<IntegrationInfo> Data
);

public record IntegrationGetResponse(
    IntegrationInfo? Data
);

public record IntegrationConnectKeyRequest(
    string Key,
    string? Label
);

public record IntegrationConnectOAuthRequest(
    string MethodID,
    Dictionary<string, string> Inputs,
    string? Label
);

public record IntegrationConnectOAuthResponse(
    IntegrationAttempt Data
);

public record IntegrationAttemptStatusResponse(
    IntegrationAttemptStatusPending Data
);

public record IntegrationAttemptCompleteRequest(
    string? Code
);
