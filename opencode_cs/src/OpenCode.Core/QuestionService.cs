using System.Collections.Concurrent;

namespace OpenCode.Core;

public record QuestionAskInput(
    string SessionId,
    Schema.QuestionInfo[] Questions,
    Schema.QuestionTool? Tool
);

public record QuestionReplyInput(
    string RequestId,
    string[][] Answers
);

public interface IQuestionService
{
    Task<string[][]> AskAsync(QuestionAskInput input);
    Task ReplyAsync(QuestionReplyInput input);
    Task RejectAsync(string requestId);
    Task<Schema.QuestionRequest[]> ListAsync();
}

public class QuestionService : IQuestionService
{
    readonly IEventService events;
    readonly ConcurrentDictionary<string, PendingQuestion> pending = new();

    public QuestionService(IEventService events)
    {
        this.events = events;
    }

    class PendingQuestion
    {
        public Schema.QuestionRequest Request { get; }
        public TaskCompletionSource<string[][]> Tcs { get; }

        public PendingQuestion(Schema.QuestionRequest request)
        {
            Request = request;
            Tcs = new TaskCompletionSource<string[][]>();
        }
    }

    public async Task<string[][]> AskAsync(QuestionAskInput input)
    {
        var id = "que_" + Guid.NewGuid().ToString("N")[..12];
        var request = new Schema.QuestionRequest(
            Id: id,
            SessionId: input.SessionId,
            Questions: input.Questions,
            Tool: input.Tool
        );

        var pendingItem = new PendingQuestion(request);
        pending[id] = pendingItem;

        await events.PublishAsync(
            new EventDefinition("question.v2.asked", false, null, 1),
            request
        );

        try
        {
            return await pendingItem.Tcs.Task;
        }
        finally
        {
            pending.TryRemove(id, out _);
        }
    }

    public async Task ReplyAsync(QuestionReplyInput input)
    {
        if (!pending.TryRemove(input.RequestId, out var pendingItem))
            throw new QuestionNotFoundException(input.RequestId);

        await events.PublishAsync(
            new EventDefinition("question.v2.replied", false, null, 1),
            new
            {
                SessionId = pendingItem.Request.SessionId,
                RequestId = input.RequestId,
                Answers = input.Answers
            }
        );

        pendingItem.Tcs.TrySetResult(input.Answers);
    }

    public async Task RejectAsync(string requestId)
    {
        if (!pending.TryRemove(requestId, out var pendingItem))
            throw new QuestionNotFoundException(requestId);

        await events.PublishAsync(
            new EventDefinition("question.v2.rejected", false, null, 1),
            new
            {
                SessionId = pendingItem.Request.SessionId,
                RequestId = requestId
            }
        );

        pendingItem.Tcs.TrySetException(new QuestionRejectedException());
    }

    public Task<Schema.QuestionRequest[]> ListAsync()
    {
        return Task.FromResult(pending.Values.Select(p => p.Request).ToArray());
    }
}

public class QuestionRejectedException : Exception
{
    public QuestionRejectedException() : base("The user dismissed this question") { }
}

public class QuestionNotFoundException : Exception
{
    public string RequestId { get; }
    public QuestionNotFoundException(string requestId) : base($"Question not found: {requestId}")
    {
        RequestId = requestId;
    }
}
