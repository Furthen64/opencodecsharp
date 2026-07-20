namespace OpenCode.Protocol;

public record QuestionRequestListResponse(
    List<QuestionRequest> Data
);

public record SessionQuestionListResponse(
    List<QuestionRequest> Data
);
