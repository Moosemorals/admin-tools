namespace CopilotWrapper.Models;

public record SessionRecord(
    string Id,
    string Title,
    DateTimeOffset CreatedAt,
    DateTimeOffset LastActiveAt);
