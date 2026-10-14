using System.Text.Json;
using Penghou.Hongxian;
using Penghou.Zhinu.Sqlite.Experimental;

namespace Marang.SessionJournal;

/// <summary>
/// Replays the runtime's durable revision outbox into Hongxian with stable append identities.
/// Hongxian stays a generic evidence ledger; delivery failure never rewrites execution history.
/// Runtime evidence contains concise decisions and verified plans, never private reasoning or provider handles.
/// </summary>
public static class AdaptiveEvidenceMirror
{
    public static async Task MirrorAsync(SqliteAdaptiveRevisionRepository source, Guid workflowId,
        ISessionEventStore events, SessionId sessionId, CancellationToken cancellationToken = default)
    {
        foreach (var row in await source.GetEvidenceAsync(workflowId, cancellationToken))
        {
            using var document = JsonDocument.Parse(row);
            var item = document.RootElement;
            var kind = item.GetProperty("Kind").GetString()!;
            var eventType = kind switch
            {
                "RevisionActivated" => SessionEventTypes.ExecutionStarted,
                "RevisionProposalCreated" => SessionEventTypes.DecisionProposed,
                "RevisionApproved" => SessionEventTypes.ApprovalGranted,
                "RevisionRejected" => SessionEventTypes.ApprovalDenied,
                "RevisionCommitted" => SessionEventTypes.RevisionAccepted,
                "OperatorDiscussion" => SessionEventTypes.UserMessage,
                _ => SessionEventTypes.ExternalEventMirrored
            };
            await events.AppendAsync(new SessionEventRequest(sessionId,
                SessionParticipantAttribution.System("adaptive-supervisor", "marang"), eventType,
                DateTimeOffset.Parse(item.GetProperty("RecordedAt").GetString()!),
                PayloadJson: row, IdempotencyKey: $"aaaw:{workflowId:D}:{item.GetProperty("Sequence").GetInt64()}"), cancellationToken);
        }
    }
}
