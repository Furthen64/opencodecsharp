namespace OpenCode.Protocol;

public record ProviderListResponse(
    List<ProviderInfo> Data
);

public record ProviderGetResponse(
    ProviderInfo Data
);
