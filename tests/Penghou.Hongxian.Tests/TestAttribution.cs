global using static Penghou.Hongxian.Tests.TestAttribution;

namespace Penghou.Hongxian.Tests;

internal static class TestAttribution
{
    public static SessionParticipantAttribution Participant(string subject) =>
        SessionParticipantAttribution.System(subject, "hongxian-tests");
}
