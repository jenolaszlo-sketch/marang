using Penghou.Fuwen;
using Penghou.Fuwen.Zhinu;
using Penghou.Zhinu;

namespace Marang.Runs;

/// <summary>Raised when the plan, map, run, and correlation fingerprints disagree.</summary>
public sealed class IncompatibleProjectionException(string message) : InvalidOperationException(message);

/// <summary>Raised when a persisted step key cannot be classified by the port map.</summary>
public sealed class ProjectionContractDriftException(string message) : InvalidOperationException(message);

/// <summary>
/// Pure construction of a <see cref="RunProjection"/> from durable upstream
/// truth. The builder reads only: a Fuwen plan (topology), its
/// <see cref="FuwenZhinuStepMap"/> (classification), a Zhinu run plus step
/// rows (execution state), a durable through-sequence (watermark), and the
/// host correlation. It invents no topology, fencing, or authority semantics.
/// </summary>
public static class RunProjectionBuilder
{
    /// <summary>Builds the projection, failing closed on fingerprint mismatch or unclassified steps.</summary>
    public static RunProjection Build(
        WorkflowPlan plan,
        FuwenZhinuStepMap map,
        WorkflowRun run,
        IReadOnlyList<WorkflowStepRun> steps,
        long throughSequence,
        RunCorrelation correlation,
        string workspace,
        string objective,
        DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(map);
        ArgumentNullException.ThrowIfNull(run);
        ArgumentNullException.ThrowIfNull(steps);
        ArgumentNullException.ThrowIfNull(correlation);
        ArgumentException.ThrowIfNullOrWhiteSpace(workspace);
        ArgumentException.ThrowIfNullOrWhiteSpace(objective);

        if (!string.Equals(map.PlanRevision, correlation.PlanRevision, StringComparison.Ordinal)
            || !string.Equals(map.ExecutionFingerprint, correlation.ExecutionFingerprint, StringComparison.Ordinal))
        {
            throw new IncompatibleProjectionException(
                "The step map does not match the correlated plan revision and execution fingerprint.");
        }

        if (run.DefinitionFingerprint is { } definitionFingerprint
            && !string.Equals(definitionFingerprint, map.ExecutionFingerprint, StringComparison.Ordinal))
        {
            throw new IncompatibleProjectionException(
                "The Zhinu run definition fingerprint does not match the step map.");
        }

        // Classify every persisted step through the port map. An unclassified
        // key is contract drift, never a silent omission.
        var matched = new List<(WorkflowStepRun Step, FuwenZhinuStepMatch Match)>(steps.Count);
        foreach (var step in steps)
        {
            if (!FuwenZhinuStepMapper.TryMatchStepKey(map, step.StepKey, out var match) || match is null)
            {
                throw new ProjectionContractDriftException(
                    $"Persisted step key '{step.StepKey}' is not classified by the step map.");
            }

            matched.Add((step, match));
        }

        var declared = Flatten(plan.Nodes).ToArray();
        var declaredPaths = declared.Select(node => node.StructuralPath).ToHashSet(StringComparer.Ordinal);
        var nodes = new List<RunProjectionNode>(declared.Length);
        foreach (var node in declared)
        {
            nodes.Add(BuildNode(node, matched));
        }

        var edges = BuildEdges(plan, declaredPaths);
        var executionSteps = matched
            .Where(pair => pair.Match.Descriptor.Origin == FuwenZhinuStepOrigin.Synthetic
                || pair.Match.Descriptor.Kind == FuwenZhinuStepKind.FanOutItem)
            .Select(pair => new RunProjectionExecutionStep(
                pair.Step.StepKey,
                pair.Match.Descriptor.Kind.ToString(),
                MapStepStatus(pair.Step.Status),
                pair.Match.Descriptor.DeclaredNodePath))
            .ToArray();
        var checkpoints = matched
            .Where(pair => pair.Step.Status == StepStatus.Waiting)
            .Select(pair => new RunProjectionCheckpoint(
                pair.Step.StepKey,
                "Waiting for supervisor input."))
            .ToArray();

        var completed = nodes.Count(node => node.Status is "completed" or "failed");
        var runStatus = MapRunStatus(run.Status);
        var attention = checkpoints.Length > 0 ? "Needs your approval." : string.Empty;
        return new RunProjection(
            run.Id.ToString("D"),
            objective,
            workspace,
            runStatus,
            correlation.PlanRevision,
            throughSequence.ToString(System.Globalization.CultureInfo.InvariantCulture),
            FormatElapsed(now - run.CreatedAt),
            $"{completed} of {nodes.Count} steps complete.",
            attention,
            nodes,
            edges,
            checkpoints,
            executionSteps,
            correlation.DelegationId.Value.ToString("D"),
            correlation.Generation);
    }

    private static RunProjectionNode BuildNode(
        WorkflowNode node,
        IReadOnlyList<(WorkflowStepRun Step, FuwenZhinuStepMatch Match)> matched)
    {
        var kind = node switch
        {
            ActivityNode => "activity",
            ContextNode => "activity",
            InferenceNode => "activity",
            CheckpointNode => "checkpoint",
            WaitNode => "checkpoint",
            ReturnNode => "completion",
            _ => "plan",
        };

        // Direct execution: a step whose declared path is this node.
        var direct = matched.FirstOrDefault(pair =>
            string.Equals(pair.Match.Descriptor.DeclaredNodePath, node.StructuralPath, StringComparison.Ordinal)
            && pair.Match.Descriptor.Origin == FuwenZhinuStepOrigin.Declared
            && pair.Match.Descriptor.Kind != FuwenZhinuStepKind.FanOutItem);
        if (direct.Step is not null)
        {
            var status = MapStepStatus(direct.Step.Status);
            return new RunProjectionNode(
                node.StructuralPath,
                node.Name,
                kind,
                status,
                "current",
                status == "failed" ? $"Failed: {node.Name}." : node.Name,
                direct.Step.Attempt,
                direct.Step.StepKey);
        }

        // Fan-out body coverage: aggregate over the item steps covering this node.
        var covering = matched
            .Where(pair => pair.Match.Descriptor.CoveredDeclaredNodePaths.Contains(node.StructuralPath))
            .Select(pair => pair.Step.Status)
            .ToArray();
        if (covering.Length > 0)
        {
            var status = covering.Any(status => status == StepStatus.Failed) ? "failed"
                : covering.Any(status => status == StepStatus.Running) ? "running"
                : covering.All(status => status == StepStatus.Completed) ? "completed"
                : "pending";
            return new RunProjectionNode(
                node.StructuralPath, node.Name, kind, status, "current", node.Name, null, null);
        }

        return new RunProjectionNode(node.StructuralPath, node.Name, kind, "pending", "current", node.Name, null, null);
    }

    private static IReadOnlyList<RunProjectionEdge> BuildEdges(WorkflowPlan plan, HashSet<string> declaredPaths)
    {
        var edges = new List<RunProjectionEdge>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        void Add(string source, string target)
        {
            if (!declaredPaths.Contains(source) || !declaredPaths.Contains(target))
            {
                return;
            }

            var id = $"{source}->{target}";
            if (seen.Add(id))
            {
                edges.Add(new RunProjectionEdge(id, source, target));
            }
        }

        if (plan.ExecutionOrder is not null)
        {
            foreach (var region in plan.ExecutionOrder.Regions)
            {
                // Phases are completion barriers in declared order: every node
                // of one phase precedes every node of the next. Single-node
                // phases therefore form a chain; multi-node phases form the
                // declared join. All edges are declared, never inferred.
                for (var phase = 1; phase < region.Phases.Count; phase++)
                {
                    foreach (var source in region.Phases[phase - 1].NodePaths)
                    {
                        foreach (var target in region.Phases[phase].NodePaths)
                        {
                            Add(source, target);
                        }
                    }
                }
            }
        }

        foreach (var node in Flatten(plan.Nodes))
        {
            var children = node switch
            {
                ConditionalNode conditional => conditional.Then.Concat(conditional.Else),
                RepeatNode repeat => repeat.Body,
                FanOutNode fanOut => fanOut.Body,
                _ => Enumerable.Empty<WorkflowNode>(),
            };
            foreach (var child in children)
            {
                Add(node.StructuralPath, child.StructuralPath);
            }
        }

        return edges;
    }

    private static IEnumerable<WorkflowNode> Flatten(IEnumerable<WorkflowNode> source)
    {
        foreach (var node in source)
        {
            yield return node;
            var children = node switch
            {
                ConditionalNode conditional => conditional.Then.Concat(conditional.Else),
                FanOutNode fanOut => fanOut.Body,
                RepeatNode repeat => repeat.Body,
                _ => Enumerable.Empty<WorkflowNode>(),
            };
            foreach (var child in Flatten(children))
            {
                yield return child;
            }
        }
    }

    private static string MapStepStatus(StepStatus status) => status switch
    {
        StepStatus.Pending => "pending",
        StepStatus.Running => "running",
        StepStatus.Completed => "completed",
        StepStatus.Failed => "failed",
        StepStatus.Waiting => "waiting",
        StepStatus.Cancelled => "failed",
        _ => "pending",
    };

    private static string MapRunStatus(WorkflowStatus status) => status switch
    {
        WorkflowStatus.Pending => "running",
        WorkflowStatus.Running => "running",
        WorkflowStatus.Completed => "completed",
        WorkflowStatus.Failed => "failed",
        WorkflowStatus.Cancelled => "failed",
        _ => "running",
    };

    private static string FormatElapsed(TimeSpan elapsed) =>
        elapsed.TotalHours >= 1
            ? $"{elapsed.TotalHours:0}h"
            : elapsed.TotalMinutes >= 1
                ? $"{elapsed.TotalMinutes:0}m"
                : $"{elapsed.TotalSeconds:0}s";
}
