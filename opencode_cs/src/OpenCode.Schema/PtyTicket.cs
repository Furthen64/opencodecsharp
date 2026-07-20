namespace OpenCode.Schema;

public static class PtyTicket
{
    public record ConnectToken(
        string Ticket,
        int ExpiresIn
    );
}
