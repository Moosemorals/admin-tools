namespace uk.osric.copilot.Web {
    internal static class CopilotEndpoints {
        /// <summary>
        /// Discovers and maps all controllers in the assembly as API routes.
        /// The actual routes are defined on <see cref="CopilotController"/> via
        /// <c>[HttpGet]</c> / <c>[HttpPost]</c> / <c>[HttpDelete]</c> attributes.
        /// Call this after <c>UseStaticFiles()</c>.
        /// </summary>
        internal static WebApplication MapCopilotApi(this WebApplication app) {
            app.MapControllers();
            return app;
        }
    }
}

