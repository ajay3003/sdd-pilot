using BirkNext.Api.Data;
using BirkNext.Api.Models;
using Microsoft.EntityFrameworkCore;

namespace BirkNext.Api.Services.ActiveEventTesting;

/// <summary>An operator-agreed range of synthetic identity values for one provider-owned scope.</summary>
public sealed record SyntheticIdentityRange(long Min, long Max);

/// <summary>
/// Generic synthetic identity policy and reservation. Providers own their scopes (which key, how many values, how values appear in an
/// event); the core owns where the agreed ranges come from and that a value is never handed out twice in an environment. Without a
/// configured range a scope cannot reserve anything — a guessed value could collide with a real record.
/// </summary>
public interface IActiveEventSyntheticIdentityReservation
{
    SyntheticIdentityRange? Range(string scope, out string reason);
    /// <summary>How many values of the range are still unused for the environment (values at or below <paramref name="floor"/> count as used).</summary>
    Task<long> AvailableAsync(string environmentId, string scope, SyntheticIdentityRange range, long? floor, CancellationToken ct);
    /// <summary>Reserves the next <paramref name="count"/> unused values, or throws when the range has too few left.</summary>
    Task<IReadOnlyList<long>> ReserveAsync(string environmentId, string scope, SyntheticIdentityRange range, long? floor, int count, Guid runId, string extensionId, CancellationToken ct);
}

/// <summary>
/// Ranges come from <c>ActiveEventTesting:SyntheticIdentityRanges:&lt;scope&gt;:Min/Max</c> (backend configuration only). Reserved values
/// are recorded in the generic <c>active_event_synthetic_identities</c> ledger; the primary key (environment, scope, value) makes a
/// concurrent double reservation fail, and the reservation then moves on to the next value.
/// </summary>
public sealed class SyntheticIdentityReservation(IConfiguration configuration, IServiceScopeFactory scopes, TimeProvider clock) : IActiveEventSyntheticIdentityReservation
{
    public const long MaxRangeSize = 10_000_000;

    public SyntheticIdentityRange? Range(string scope, out string reason)
    {
        var section = configuration.GetSection("ActiveEventTesting:SyntheticIdentityRanges").GetSection(scope);
        if (!long.TryParse(section["Min"], out var min) || !long.TryParse(section["Max"], out var max))
        {
            reason = $"No reserved synthetic identity range is configured for {scope} (ActiveEventTesting:SyntheticIdentityRanges). A guessed value could collide with a real record.";
            return null;
        }
        if (min <= 0 || min > max || max - min >= MaxRangeSize)
        {
            reason = $"The synthetic identity range for {scope} is invalid (positive, min ≤ max, at most {MaxRangeSize:N0} values).";
            return null;
        }
        reason = $"Reserved synthetic range {min}–{max} for {scope}.";
        return new SyntheticIdentityRange(min, max);
    }

    public async Task<long> AvailableAsync(string environmentId, string scope, SyntheticIdentityRange range, long? floor, CancellationToken ct)
    {
        var next = await NextAsync(environmentId, scope, range, floor, ct);
        return Math.Max(0, range.Max - next + 1);
    }

    public async Task<IReadOnlyList<long>> ReserveAsync(string environmentId, string scope, SyntheticIdentityRange range, long? floor, int count, Guid runId, string extensionId, CancellationToken ct)
    {
        if (count < 1) throw new ArgumentOutOfRangeException(nameof(count));
        for (var attempt = 0; attempt < 5; attempt++)
        {
            var next = await NextAsync(environmentId, scope, range, floor, ct);
            if (next + count - 1 > range.Max) throw new InvalidOperationException($"The reserved synthetic range for {scope} has too few unused values.");
            var values = Enumerable.Range(0, count).Select(offset => next + offset).ToArray();
            using var scopeServices = scopes.CreateScope();
            var db = scopeServices.ServiceProvider.GetRequiredService<AppDbContext>();
            var now = clock.GetUtcNow();
            db.ActiveEventSyntheticIdentities.AddRange(values.Select(value => new ActiveEventSyntheticIdentityRecord
            {
                EnvironmentId = environmentId, Scope = scope, Value = value, RunId = runId, ExtensionId = extensionId, ReservedAt = now,
            }));
            try
            {
                await db.SaveChangesAsync(ct);
                return values;
            }
            catch (DbUpdateException) { /* another run took one of these values: recompute from the ledger */ }
        }
        throw new InvalidOperationException($"Synthetic identities for {scope} could not be reserved (concurrent reservations).");
    }

    private async Task<long> NextAsync(string environmentId, string scope, SyntheticIdentityRange range, long? floor, CancellationToken ct)
    {
        using var scopeServices = scopes.CreateScope();
        var db = scopeServices.ServiceProvider.GetRequiredService<AppDbContext>();
        var used = await db.ActiveEventSyntheticIdentities.AsNoTracking()
            .Where(item => item.EnvironmentId == environmentId && item.Scope == scope && item.Value >= range.Min && item.Value <= range.Max)
            .MaxAsync(item => (long?)item.Value, ct);
        // A local data reset clears the ledger but keeps the highest value per scope, so a value that exists downstream is never reused.
        var resetFloor = scopeServices.ServiceProvider.GetService<LocalDataReset.LocalDataResetState>()?.SyntheticIdentityFloor(environmentId, scope);
        var known = new[] { used, floor, resetFloor }.OfType<long>().ToArray();
        if (known.Any(value => value > range.Max)) return range.Max + 1; // a floor beyond the range means the range is used up
        return known.Where(value => value >= range.Min).DefaultIfEmpty(range.Min - 1).Max() + 1;
    }
}
