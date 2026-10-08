namespace FloraBot.Api.Jobs;

public sealed class FloraJobs(JobRunner runner, IConfiguration configuration, ILogger<FloraJobs> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var seconds = configuration.GetValue("JOBS_INTERVAL_SECONDS", 30);
        if (seconds is < 5 or > 300) throw new InvalidOperationException("JOBS_INTERVAL_SECONDS must be between 5 and 300.");
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(seconds));
        do
        {
            foreach (var name in JobRunner.Names)
            {
                if (stoppingToken.IsCancellationRequested) return;
                try
                {
                    var result = await runner.RunAsync(name, stoppingToken);
                    if (result.Executed && result.Affected > 0) logger.LogInformation("Job {Job} processed {Count} rows", name, result.Affected);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { return; }
                catch (Exception ex) { logger.LogError(ex, "Job {Job} failed; the transaction is rolled back and a later tick will retry", name); }
            }
        } while (await timer.WaitForNextTickAsync(stoppingToken));
    }
}
