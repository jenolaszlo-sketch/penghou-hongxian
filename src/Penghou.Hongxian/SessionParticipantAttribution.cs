namespace Penghou.Hongxian;

/// <summary>
/// Immutable host-supplied attribution claim. Attribution is audit metadata;
/// it does not authenticate the subject or grant capabilities.
/// </summary>
public sealed record SessionParticipantAttribution(
    string Kind,
    string Provider,
    string Subject,
    string? DisplayName = null)
{
    public static SessionParticipantAttribution Human(
        string subject,
        string provider = "host",
        string? displayName = null) =>
        new(SessionParticipantKinds.Human, provider, subject, displayName);

    public static SessionParticipantAttribution Agent(
        string subject,
        string provider = "host",
        string? displayName = null) =>
        new(SessionParticipantKinds.Agent, provider, subject, displayName);

    public static SessionParticipantAttribution System(
        string subject,
        string provider = "host",
        string? displayName = null) =>
        new(SessionParticipantKinds.System, provider, subject, displayName);

    public static SessionParticipantAttribution Tool(
        string subject,
        string provider = "host",
        string? displayName = null) =>
        new(SessionParticipantKinds.Tool, provider, subject, displayName);

    public override string ToString() =>
        DisplayName is null
            ? $"{Kind}:{Provider}:{Subject}"
            : $"{DisplayName} ({Kind}:{Provider}:{Subject})";
}

public static class SessionParticipantKinds
{
    public const string Human = "human";
    public const string Agent = "agent";
    public const string Model = "model";
    public const string Tool = "tool";
    public const string WorkflowActivity = "workflow-activity";
    public const string System = "system";
    public const string External = "external";
    public const string Legacy = "legacy";
}
