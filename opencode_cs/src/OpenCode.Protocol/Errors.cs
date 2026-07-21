namespace OpenCode.Protocol;

public record ApiError(
    string Type,
    string Message
);

public record InvalidRequestError(
    string Message,
    string? Kind = null,
    string? Field = null
) : ApiError("InvalidRequestError", Message);

public record UnauthorizedError(
    string Message
) : ApiError("UnauthorizedError", Message);

public record ConflictError(
    string Message,
    string? Resource = null
) : ApiError("ConflictError", Message);

public record ServiceUnavailableError(
    string Message,
    string? Service = null
) : ApiError("ServiceUnavailableError", Message);

public record UnknownError(
    string Message,
    string? Ref = null
) : ApiError("UnknownError", Message);

public record ProviderNotFoundError(
    string ProviderID,
    string Message
) : ApiError("ProviderNotFoundError", Message);

public record SessionNotFoundError(
    string SessionID,
    string Message
) : ApiError("SessionNotFoundError", Message);

public record SessionBusyError(
    string SessionID,
    string Message
) : ApiError("SessionBusyError", Message);

public record MessageNotFoundError(
    string SessionID,
    string MessageID,
    string Message
) : ApiError("MessageNotFoundError", Message);

public record InvalidCursorError(
    string Message
) : ApiError("InvalidCursorError", Message);

public record PermissionNotFoundError(
    string RequestID,
    string Message
) : ApiError("PermissionNotFoundError", Message);

public record QuestionNotFoundError(
    string RequestID,
    string Message
) : ApiError("QuestionNotFoundError", Message);

public record ForbiddenError(
    string Message
) : ApiError("ForbiddenError", Message);

public record PtyNotFoundError(
    string PtyID,
    string Message
) : ApiError("PtyNotFoundError", Message);
