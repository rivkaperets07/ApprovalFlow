/// <summary>
/// Keys for the two StateIndex-backed lists SubmissionEndpoints and ApproverEndpoints
/// both read/write — kept in one place so the two files can't drift on the key string.
/// </summary>
public static class GatewayIndexKeys
{
    public const string Submitted = "index-submitted";
    public const string Escalated = "index-escalated";
}
