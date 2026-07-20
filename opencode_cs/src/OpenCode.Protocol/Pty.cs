namespace OpenCode.Protocol;

public record PtyListResponse(
    List<PtyInfo> Data
);

public record PtyGetResponse(
    PtyInfo Data
);

public record PtyUpdateResponse(
    PtyInfo Data
);

public record PtyConnectTokenResponse(
    PtyTicket.ConnectToken Data
);
