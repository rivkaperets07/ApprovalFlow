namespace ApprovalFlow.Contracts;

/// <summary>GLOBAL-RECEIPT / GLOBAL-MATH: one itemized line of an invoice, provided
/// directly by the submitter (no OCR) and validated against the claimed total.</summary>
public record LineItem(string Description, decimal Amount);
