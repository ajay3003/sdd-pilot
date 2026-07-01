using System.Text.Json;
using System.Text.Json.Serialization;
using BirkNext.Web.Models;
using Microsoft.JSInterop;

namespace BirkNext.Web.Services;

public interface IIntegrationTargetRegistryService
{
    IntegrationTargetRegistry Registry { get; }
    Task LoadAsync(IJSRuntime js);
    Task SaveAsync(IJSRuntime js);
    void UpsertFromHints(IEnumerable<IntegrationTargetHint> hints, string projectName);
}

public sealed class IntegrationTargetRegistryService : IIntegrationTargetRegistryService
{
    private const string StorageKey = "birknext:integration-target-registry";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter() }
    };

    private bool _isLoaded;
    private IntegrationTargetRegistry _registry = new();

    public IntegrationTargetRegistry Registry => _registry;

    public async Task LoadAsync(IJSRuntime js)
    {
        if (_isLoaded) return;

        try
        {
            var json = await js.InvokeAsync<string?>("birkNextStorage.getItem", StorageKey);
            if (!string.IsNullOrWhiteSpace(json))
                _registry = JsonSerializer.Deserialize<IntegrationTargetRegistry>(json, JsonOptions) ?? new();
        }
        catch
        {
            _registry = new IntegrationTargetRegistry();
        }

        _isLoaded = true;
    }

    public async Task SaveAsync(IJSRuntime js)
    {
        try
        {
            var json = JsonSerializer.Serialize(_registry, JsonOptions);
            await js.InvokeVoidAsync("birkNextStorage.setItem", StorageKey, json);
        }
        catch
        {
            // Local storage is an optional convenience for runtime review hints.
        }
    }

    public void UpsertFromHints(IEnumerable<IntegrationTargetHint> hints, string projectName)
    {
        foreach (var hint in hints.Where(HasMeaningfulIntegrationValue))
        {
            if (string.IsNullOrWhiteSpace(hint.Name))
                hint.Name = $"{projectName} {hint.ProviderType}".Trim();

            var existing = _registry.Entries.FirstOrDefault(entry =>
                string.Equals(entry.Name, hint.Name, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(entry.ProviderType, hint.ProviderType, StringComparison.OrdinalIgnoreCase));

            if (existing is null)
            {
                hint.Id = string.IsNullOrWhiteSpace(hint.Id) ? Guid.NewGuid().ToString("N") : hint.Id;
                _registry.Entries.Add(Clone(hint));
            }
            else
            {
                existing.Endpoint = hint.Endpoint;
                existing.Namespace = hint.Namespace;
                existing.Resource = hint.Resource;
                existing.Topic = hint.Topic;
                existing.Queue = hint.Queue;
                existing.ConsumerGroup = hint.ConsumerGroup;
                existing.Subscription = hint.Subscription;
                existing.AuthType = hint.AuthType;
                existing.EnvironmentHint = hint.EnvironmentHint;
                existing.Source = hint.Source;
            }
        }
    }

    private static bool HasMeaningfulIntegrationValue(IntegrationTargetHint hint) =>
        !string.IsNullOrWhiteSpace(hint.ProviderType) &&
        (!string.IsNullOrWhiteSpace(hint.Endpoint) ||
         !string.IsNullOrWhiteSpace(hint.Namespace) ||
         !string.IsNullOrWhiteSpace(hint.Resource) ||
         !string.IsNullOrWhiteSpace(hint.Topic) ||
         !string.IsNullOrWhiteSpace(hint.Queue) ||
         !string.IsNullOrWhiteSpace(hint.ConsumerGroup) ||
         !string.IsNullOrWhiteSpace(hint.Subscription));

    private static IntegrationTargetHint Clone(IntegrationTargetHint source) =>
        new()
        {
            Id = source.Id,
            Name = source.Name,
            ProviderType = source.ProviderType,
            Endpoint = source.Endpoint,
            Namespace = source.Namespace,
            Resource = source.Resource,
            Topic = source.Topic,
            Queue = source.Queue,
            ConsumerGroup = source.ConsumerGroup,
            Subscription = source.Subscription,
            AuthType = source.AuthType,
            EnvironmentHint = source.EnvironmentHint,
            Source = source.Source
        };
}
