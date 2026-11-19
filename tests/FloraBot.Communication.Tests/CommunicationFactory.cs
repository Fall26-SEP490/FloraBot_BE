using FloraBot.Api.Modules.Notify;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace FloraBot.Communication.Tests;

public sealed class FakeEmailProvider : ITransactionalEmailSender
{
    public int Calls;
    public bool Fail;
    public TaskCompletionSource? Entered;
    public TaskCompletionSource? Continue;
    public TransactionalEmail? LastMessage;

    public async Task SendAsync(TransactionalEmail message, CancellationToken ct)
    {
        Interlocked.Increment(ref Calls);
        LastMessage = message;
        Entered?.TrySetResult();
        if (Continue is not null) await Continue.Task.WaitAsync(ct);
        if (Fail) throw new EmailUnavailableException();
    }
}

public sealed class CommunicationFactory : WebApplicationFactory<CommunicationProgram>
{
    public const string Token = "communication-tests-only-token-at-least-32-characters";
    public FakeEmailProvider Provider { get; } = new();

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");
        builder.UseSetting("COMMUNICATION_SERVICE_TOKEN", Token);
        builder.UseSetting("COMMUNICATION_DATABASE_URL", Environment.GetEnvironmentVariable("TEST_COMMUNICATION_DATABASE_URL")
            ?? "Host=localhost;Port=55436;Database=florabot_communication_tests;Username=florabot;Password=local-florabot-only");
        builder.UseSetting("EMAIL_PROVIDER", "Brevo");
        builder.UseSetting("EMAIL_SENDER", "sender@example.test");
        builder.UseSetting("BREVO_API_KEY", "fake-provider-credential");
        builder.ConfigureServices(services =>
        {
            services.RemoveAll<ITransactionalEmailSender>();
            services.AddSingleton<ITransactionalEmailSender>(Provider);
        });
    }
}
