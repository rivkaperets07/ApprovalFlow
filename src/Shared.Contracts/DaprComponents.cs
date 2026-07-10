namespace ApprovalFlow.Contracts;

/// <summary>Names of the Dapr building blocks every service's sidecar is wired to
/// (docker-compose + components/). A typo in one of these doesn't fail compilation — it
/// silently targets a component that doesn't exist — so they live here, once.</summary>
public static class DaprComponents
{
    public const string StateStore = "statestore";
    public const string PubSub = "pubsub";
}
