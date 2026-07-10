using Dapr.Client;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using System.Text.Json;

/// <summary>
/// Admin-only config surface: add/update a vendor directory entry, or read/replace the
/// live policy document — both proxied to DecisionEngine over Dapr service invocation,
/// same pattern as SubmissionEndpoints.GetVendorsAsync. DecisionEngine has no auth
/// of its own (reachable only over the sidecar, never directly), so this admin gate is
/// the only access control on either operation. Both take effect immediately — no
/// restart — because DecisionEngine's writes land in the same files IConfiguration
/// already loads with reloadOnChange (ADR 004).
/// </summary>
public static class AdminEndpoints
{
    public static void MapAdminEndpoints(this WebApplication app)
    {
        app.MapPost("/vendors", CreateVendorAsync).RequireAuthorization(AuthPolicies.Admin);
        app.MapGet("/policy", GetPolicyAsync).RequireAuthorization(AuthPolicies.Admin);
        app.MapPut("/policy", UpdatePolicyAsync).RequireAuthorization(AuthPolicies.Admin);
    }

    private static async Task<IResult> CreateVendorAsync([FromBody] VendorEntry? request, DaprClient daprClient, Bulkhead decisionEngineBulkhead, ILogger<Program> logger)
    {
        var vendor = request?.Vendor?.Trim() ?? string.Empty;
        var category = request?.Category?.Trim() ?? string.Empty;
        if (vendor.Length == 0 || category.Length == 0)
        {
            return Results.BadRequest(new { error = "Vendor and Category are both required." });
        }

        try
        {
            // Bulkhead (Bulkhead.cs): shared cap across every Gateway->DecisionEngine
            // call, so a slow/overloaded DecisionEngine can't exhaust Gateway's own
            // resources regardless of which admin/submitter endpoint is calling it.
            var saved = await decisionEngineBulkhead.ExecuteAsync(
                () => daprClient.InvokeMethodAsync<VendorEntry, VendorEntry>(HttpMethod.Post, "decision-engine", "vendors", new VendorEntry(vendor, category)));
            logger.LogInformation("Admin set vendor directory entry {Vendor} -> {Category}.", saved.Vendor, saved.Category);
            return Results.Ok(saved);
        }
        catch (BulkheadRejectedException)
        {
            logger.LogWarning("Rejected a vendor-directory write: the DecisionEngine bulkhead is already at capacity.");
            return Results.Problem(detail: "The vendor directory is temporarily unavailable (too many concurrent DecisionEngine calls).", statusCode: StatusCodes.Status503ServiceUnavailable);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Could not reach DecisionEngine to update the vendor directory.");
            return Results.Problem(detail: "The vendor directory is temporarily unavailable (DecisionEngine unreachable).", statusCode: StatusCodes.Status503ServiceUnavailable);
        }
    }

    private static async Task<IResult> GetPolicyAsync(DaprClient daprClient, Bulkhead decisionEngineBulkhead, ILogger<Program> logger)
    {
        try
        {
            var policy = await decisionEngineBulkhead.ExecuteAsync(
                () => daprClient.InvokeMethodAsync<JsonElement>(HttpMethod.Get, "decision-engine", "policy"));
            return Results.Ok(policy);
        }
        catch (BulkheadRejectedException)
        {
            logger.LogWarning("Rejected a policy read: the DecisionEngine bulkhead is already at capacity.");
            return Results.Problem(detail: "The policy document is temporarily unavailable (too many concurrent DecisionEngine calls).", statusCode: StatusCodes.Status503ServiceUnavailable);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Could not reach DecisionEngine to read the policy document.");
            return Results.Problem(detail: "The policy document is temporarily unavailable (DecisionEngine unreachable).", statusCode: StatusCodes.Status503ServiceUnavailable);
        }
    }

    // Validated here too (not just in DecisionEngine) so a structurally bad document is a
    // 400 the admin can act on, instead of getting misreported as the 503 the catch below
    // exists for — that block is for DecisionEngine actually being unreachable, not for
    // DecisionEngine deliberately rejecting the payload.
    private static async Task<IResult> UpdatePolicyAsync([FromBody] JsonElement policyDocument, DaprClient daprClient, Bulkhead decisionEngineBulkhead, ILogger<Program> logger)
    {
        if (policyDocument.ValueKind != JsonValueKind.Object ||
            !policyDocument.TryGetProperty("GlobalGuardrails", out _) ||
            !policyDocument.TryGetProperty("ExpensePolicies", out _))
        {
            return Results.BadRequest(new { error = "Policy document must be a JSON object with \"GlobalGuardrails\" and \"ExpensePolicies\" top-level sections." });
        }

        try
        {
            await decisionEngineBulkhead.ExecuteAsync(
                () => daprClient.InvokeMethodAsync(HttpMethod.Put, "decision-engine", "policy", policyDocument));
            logger.LogInformation("Admin replaced the policy document.");
            return Results.Ok(new { message = "Policy updated." });
        }
        catch (BulkheadRejectedException)
        {
            logger.LogWarning("Rejected a policy write: the DecisionEngine bulkhead is already at capacity.");
            return Results.Problem(detail: "The policy document is temporarily unavailable (too many concurrent DecisionEngine calls).", statusCode: StatusCodes.Status503ServiceUnavailable);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Could not reach DecisionEngine to update the policy document.");
            return Results.Problem(detail: "The policy document is temporarily unavailable (DecisionEngine unreachable).", statusCode: StatusCodes.Status503ServiceUnavailable);
        }
    }
}
