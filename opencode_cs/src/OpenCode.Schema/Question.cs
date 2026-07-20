using System.Text.Json.Serialization;

namespace OpenCode.Schema;

public record QuestionOption(
    string Label,
    string Description
);

public record QuestionInfo(
    string Question,
    string Header,
    QuestionOption[] Options,
    bool? Multiple,
    bool? Custom
);

public record QuestionPrompt(
    string Question,
    string Header,
    QuestionOption[] Options,
    bool? Multiple
);

public record QuestionTool(
    string MessageId,
    string CallId
);

public record QuestionRequest(
    string Id,
    string SessionId,
    QuestionInfo[] Questions,
    QuestionTool? Tool
);

public record QuestionReply(
    string[][] Answers
);
