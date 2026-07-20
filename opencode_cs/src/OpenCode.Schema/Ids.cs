using System.Text.Json.Serialization;

namespace OpenCode.Schema;

public static class SessionId
{
    public static string Create() => "ses_" + Identifier.Ascending();
}

public static class MessageId
{
    public static string Create() => "msg_" + Identifier.Ascending();
}

public static class EventId
{
    public static string Create() => "evt_" + Identifier.Ascending();
}

public static class PermissionId
{
    public static string Create() => "per_" + Identifier.Ascending();
}

public static class QuestionId
{
    public static string Create() => "que_" + Identifier.Ascending();
}

public static class PtyId
{
    public static string Create() => "pty_" + Identifier.Ascending();
}

public static class CredentialId
{
    public static string Create() => "cred_" + Identifier.Ascending();
}

public static class IntegrationAttemptId
{
    public static string Create() => "con_" + Identifier.Ascending();
}
