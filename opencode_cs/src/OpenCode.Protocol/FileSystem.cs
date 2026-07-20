namespace OpenCode.Protocol;

public record FsListResponse(
    List<FileSystemEntry> Data
);

public record FsFindQuery(
    LocationQuery? Location,
    string Query,
    string Type,
    int? Limit
);
