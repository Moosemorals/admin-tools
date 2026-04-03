namespace uk.osric.copilot.Models {
    /// <summary>Request body for <c>POST /user-input-reply</c>.</summary>
    internal sealed record UserInputReply(string RequestId, string? Answer, bool WasFreeform);

    /// <summary>Optional request body for <c>POST /sessions</c>.</summary>
    internal sealed record CreateSessionRequest(string? WorkingDirectory);
}
