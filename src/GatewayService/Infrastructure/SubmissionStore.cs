using Dapr.Client;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

public interface ISubmissionStore
{
    Task<bool> HasBeenSubmittedAsync(string trackingId);
    Task MarkSubmittedAsync(string trackingId);
}

public interface IDaprStateClient
{
    Task<T> GetStateAsync<T>(string storeName, string key, ConsistencyMode? consistencyMode, IReadOnlyDictionary<string, string>? metadata, CancellationToken cancellationToken);
    Task SaveStateAsync<T>(string storeName, string key, T value, StateOptions? stateOptions, IReadOnlyDictionary<string, string>? metadata, CancellationToken cancellationToken);
}

public class DaprStateClient : IDaprStateClient
{
    private readonly DaprClient _daprClient;

    public DaprStateClient(DaprClient daprClient)
    {
        _daprClient = daprClient;
    }

    public Task<T> GetStateAsync<T>(string storeName, string key, ConsistencyMode? consistencyMode, IReadOnlyDictionary<string, string>? metadata, CancellationToken cancellationToken)
    {
        return _daprClient.GetStateAsync<T>(storeName, key, consistencyMode, metadata, cancellationToken);
    }

    public Task SaveStateAsync<T>(string storeName, string key, T value, StateOptions? stateOptions, IReadOnlyDictionary<string, string>? metadata, CancellationToken cancellationToken)
    {
        return _daprClient.SaveStateAsync(storeName, key, value, stateOptions, metadata, cancellationToken);
    }
}

public static class GatewayStateKeys
{
    public const string StateStoreName = DaprComponents.StateStore;

    public static string GetSubmittedKey(string trackingId) => $"invoice-{trackingId}-submitted";
}

public class DaprSubmissionStore : ISubmissionStore
{
    private readonly IDaprStateClient _daprClient;

    public DaprSubmissionStore(IDaprStateClient daprClient)
    {
        _daprClient = daprClient;
    }

    public Task<bool> HasBeenSubmittedAsync(string trackingId)
    {
        return _daprClient.GetStateAsync<bool>(GatewayStateKeys.StateStoreName, GatewayStateKeys.GetSubmittedKey(trackingId), null, null, CancellationToken.None);
    }

    public Task MarkSubmittedAsync(string trackingId)
    {
        return _daprClient.SaveStateAsync(GatewayStateKeys.StateStoreName, GatewayStateKeys.GetSubmittedKey(trackingId), true, null, null, CancellationToken.None);
    }
}
