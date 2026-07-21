namespace OpenCode.Core;

public record QuestionToolInput(
    Schema.QuestionPrompt[] Questions
);

public record QuestionToolOutput(
    string[][] Answers
);

public class QuestionToolImpl : Tool
{
    readonly IQuestionService questionService;
    readonly IPermissionService permission;

    public QuestionToolImpl(IQuestionService questionService, IPermissionService permission)
        : base("question",
            "Use this tool when you need to ask the user questions during execution. This allows you to:\n" +
            "1. Gather user preferences or requirements\n" +
            "2. Clarify ambiguous instructions\n" +
            "3. Get decisions on implementation choices as you work\n" +
            "4. Offer choices to the user about what direction to take.\n\n" +
            "Usage notes:\n" +
            "- When `custom` is enabled (default), a \"Type your own answer\" option is added automatically; don't include \"Other\" or catch-all options\n" +
            "- Answers are returned as arrays of labels; set `multiple: true` to allow selecting more than one\n" +
            "- If you recommend a specific option, make that the first option in the list and add \"(Recommended)\" at the end of the label")
    {
        this.questionService = questionService;
        this.permission = permission;
    }

    public override async Task<ToolOutput> ExecuteAsync(object input, ToolContext context)
    {
        if (input is not QuestionToolInput questionInput)
            throw new ToolFailure("Invalid input for question tool");

        await permission.AssertAsync(new PermissionAssertInput(
            Id: null,
            SessionId: context.SessionId,
            Action: "question",
            Resources: new[] { "*" },
            Save: null,
            Metadata: null,
            Source: new Schema.PermissionSource("tool", context.AssistantMessageId, context.ToolCallId),
                Agent: context.AgentId
        ));

        var infos = questionInput.Questions.Select(q => new Schema.QuestionInfo(
            Question: q.Question,
            Header: q.Header,
            Options: q.Options,
            Multiple: q.Multiple,
            Custom: null
        )).ToArray();

        var answers = await questionService.AskAsync(new QuestionAskInput(
            SessionId: context.SessionId,
            Questions: infos,
            Tool: new Schema.QuestionTool(context.AssistantMessageId, context.ToolCallId)
        ));

        return new ToolOutput(
            new List<ToolOutputContent>
            {
                new ToolTextContent { Text = FormatModelOutput(questionInput.Questions, answers) }
            },
            new QuestionToolOutput(answers)
        );
    }

    static string FormatModelOutput(Schema.QuestionPrompt[] questions, string[][] answers)
    {
        var parts = new List<string>();
        for (int i = 0; i < questions.Length; i++)
        {
            var answer = i < answers.Length && answers[i].Length > 0
                ? string.Join(", ", answers[i])
                : "Unanswered";
            parts.Add($"\"{questions[i].Question}\"=\"{answer}\"");
        }
        return $"User has answered your questions: {string.Join(", ", parts)}. You can now continue with the user's answers in mind.";
    }

    public override ToolDefinition ToDefinition(string name)
    {
        return new ToolDefinition(
            name,
            Description,
            new { questions = "(QuestionPrompt[]) Questions to ask" },
            null
        );
    }
}
