namespace ApprovalFlow.Contracts;

/// <summary>Payment outcome published by PaymentService on <c>payment.completed</c>
/// (including Saga compensation), consumed by the Gateway to update <c>/status</c>.</summary>
public record PaymentResult(string TrackingId, bool Success, string Message);
