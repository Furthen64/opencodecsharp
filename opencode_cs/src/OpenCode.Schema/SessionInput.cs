using System.Text.Json.Serialization;

namespace OpenCode.Schema;

public enum SessionDelivery
{
    Steer,
    Queue
}

public record SessionInputAdmitted(
    long AdmittedSeq,
    string MessageId,
    string SessionId,
    Prompt Prompt,
    SessionDelivery Delivery,
    long TimeCreated,
    long? PromotedSeq
);
