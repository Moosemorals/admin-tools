namespace uk.osric.copilot.Models;

public record SessionRecord(
    string Id,
    string Title,
    DateTimeOffset CreatedAt,
    DateTimeOffset LastActiveAt);
