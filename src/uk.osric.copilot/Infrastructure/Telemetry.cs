// SPDX-FileCopyrightText: Copyright (c) 2026 Osric Wilkinson
// SPDX-License-Identifier: MIT

namespace uk.osric.copilot.Infrastructure {
    using System.Diagnostics;

    /// <summary>Shared OpenTelemetry instrumentation primitives.</summary>
    internal static class CopilotTelemetry {
        internal static readonly ActivitySource ActivitySource = new("uk.osric.copilot");
    }
}
