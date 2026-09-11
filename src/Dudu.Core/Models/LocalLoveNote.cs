namespace Dudu.Core.Models;

public sealed record LocalLoveNote(
    string Id,
    string Text,
    bool Enabled = true);
