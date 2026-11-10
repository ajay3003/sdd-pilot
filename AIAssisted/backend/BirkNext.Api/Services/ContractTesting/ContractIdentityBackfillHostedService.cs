namespace BirkNext.Api.Services.ContractTesting;

/// <summary>
/// Startup run of the legacy contract identity resolution (<see cref="ContractRegistryService.ResolveLegacyIdentitiesAsync"/>): contracts stored
/// before project scope get their project when every version resolves to one project; the rest are marked Unscoped (uploads) or NeedsReview. The
/// registry also runs it lazily before classifying, so the order of startup and the first import does not matter. Never merges or deletes.
/// </summary>
public sealed class ContractIdentityBackfillHostedService(IServiceProvider services, ILogger<ContractIdentityBackfillHostedService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(3), stoppingToken);
            using var scope = services.CreateScope();
            var (resolved, unscoped, review) = await scope.ServiceProvider.GetRequiredService<ContractRegistryService>().ResolveLegacyIdentitiesAsync(stoppingToken);
            if (resolved + unscoped + review > 0)
                logger.LogInformation("Contract identity: {Resolved} legacy contracts assigned their project, {Unscoped} unscoped (upload/URL), {Review} need an identity review", resolved, unscoped, review);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { logger.LogWarning(ex, "Contract identity backfill skipped"); }
    }
}
