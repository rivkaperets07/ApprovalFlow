using System.Diagnostics;

namespace DecisionEngine.Core.Logic;

/// <summary>
/// N4: the <see cref="ActivitySource"/> for this service's own custom spans (the AI
/// provider call, chiefly — "the agent's model/tool calls" the assignment calls out by
/// name). Registered with the OpenTelemetry SDK via <c>.AddSource(ActivitySourceName)</c>
/// in DecisionEngine.cs, so a span started here nests inside the auto-instrumented
/// ASP.NET Core span for whichever HTTP/pub-sub request triggered it, landing in the same
/// distributed trace instead of starting a disconnected one. Every span this source
/// starts should carry a "correlation_id" tag set to the invoice's TrackingId — the same
/// id every structured log line already carries (M14) — so a trace can be found from a
/// log line and vice versa.
/// </summary>
public static class DecisionEngineTelemetry
{
    public const string ActivitySourceName = "ApprovalFlow.DecisionEngine";
    public static readonly ActivitySource ActivitySource = new(ActivitySourceName);
}
