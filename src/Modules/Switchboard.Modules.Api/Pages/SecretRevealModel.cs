namespace Switchboard.Modules.Api.Pages;

/// <summary>A secret shown exactly once, with a copy button.</summary>
public sealed record SecretRevealModel(string Id, string Title, string Secret, string? Hint = null);
