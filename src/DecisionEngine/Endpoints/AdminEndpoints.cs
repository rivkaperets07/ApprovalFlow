using DecisionEngine.Core.Logic;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using System.Text.Json;

namespace DecisionEngine.Endpoints;

/// <summary>
/// Admin-facing config surface: add/update a vendor directory entry, or read/replace the
/// whole policy document. No auth here — DecisionEngine takes no traffic except over the
/// Dapr sidecar (internal-only), so the Gateway's admin-gated proxies (its own
/// AdminEndpoints.cs) are the real access control on both operations. Both write paths
/// reuse the exact files IConfiguration already loads with reloadOnChange (ADR 004), so a
/// write here needs no restart to take effect on the next evaluation.
/// </summary>
public static class AdminEndpoints
{
    public static void MapAdminEndpoints(this WebApplication app)
    {
        app.MapPost("/vendors", AddVendorAsync);
        app.MapGet("/policy", GetPolicyAsync);
        app.MapPut("/policy", UpdatePolicyAsync);
    }

    private static async Task<IResult> AddVendorAsync([FromBody] VendorEntry? request, IWebHostEnvironment env, ILogger<Program> logger)
    {
        var vendor = request?.Vendor?.Trim() ?? string.Empty;
        var category = request?.Category?.Trim() ?? string.Empty;
        if (vendor.Length == 0 || category.Length == 0)
        {
            return Results.BadRequest(new { error = "Vendor and Category are both required." });
        }

        var path = Path.Combine(env.ContentRootPath, "Ai", "vendor-directory.json");
        var saved = await VendorDirectoryFile.AddOrUpdateAsync(path, vendor, category);

        logger.LogInformation("Vendor directory entry set: {Vendor} -> {Category}.", saved.Vendor, saved.Category);
        return Results.Ok(saved);
    }

    private static async Task<IResult> GetPolicyAsync(IWebHostEnvironment env)
    {
        var path = Path.Combine(env.ContentRootPath, "Policies", "policies.json");
        var json = await PolicyFile.ReadAsync(path);
        return Results.Content(json, "application/json");
    }

    private static async Task<IResult> UpdatePolicyAsync([FromBody] JsonElement policyDocument, IWebHostEnvironment env, ILogger<Program> logger)
    {
        if (!IsValidPolicyDocument(policyDocument, out var error))
        {
            return Results.BadRequest(new { error });
        }

        var path = Path.Combine(env.ContentRootPath, "Policies", "policies.json");
        var json = JsonSerializer.Serialize(policyDocument, new JsonSerializerOptions { WriteIndented = true });
        await PolicyFile.WriteAsync(path, json);

        logger.LogInformation("policies.json replaced via admin PUT /policy.");
        return Results.Ok(new { message = "Policy updated." });
    }

    // Deliberately shallow: valid JSON object with the two top-level sections
    // IConfiguration expects. See PolicyFile for why this doesn't go deeper.
    private static bool IsValidPolicyDocument(JsonElement policyDocument, out string error)
    {
        if (policyDocument.ValueKind != JsonValueKind.Object ||
            !policyDocument.TryGetProperty("GlobalGuardrails", out _) ||
            !policyDocument.TryGetProperty("ExpensePolicies", out _))
        {
            error = "Policy document must be a JSON object with \"GlobalGuardrails\" and \"ExpensePolicies\" top-level sections.";
            return false;
        }

        error = string.Empty;
        return true;
    }
}
