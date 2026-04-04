// SPDX-FileCopyrightText: Copyright (c) 2026 Osric Wilkinson
// SPDX-License-Identifier: MIT

namespace uk.osric.copilot.Models {
    /// <summary>Request body for <c>POST /user-input-reply</c>.</summary>
    public sealed record UserInputReply(string RequestId, string? Answer, bool WasFreeform);

    /// <summary>Optional request body for <c>POST /sessions</c>.</summary>
    public sealed record CreateSessionRequest(string? WorkingDirectory);
}
