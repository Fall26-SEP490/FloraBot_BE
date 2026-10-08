using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using FloraBot.Api.Modules.KioskOps;
using MQTTnet;
using MQTTnet.Formatter;
using MQTTnet.Protocol;

namespace FloraBot.Api.Infrastructure;

public sealed class MqttEventWorker(IConfiguration configuration, IServiceScopeFactory scopes, ILogger<MqttEventWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var credentialFile = configuration["MQTT_CREDENTIALS_FILE"] ?? throw new InvalidOperationException("MQTT_CREDENTIALS_FILE is required.");
        using var credentials = JsonDocument.Parse(await File.ReadAllTextAsync(credentialFile, stoppingToken));
        var password = credentials.RootElement.GetProperty("florabot-backend").GetProperty("password").GetString();
        using var ca = X509CertificateLoader.LoadCertificateFromFile(configuration["MQTT_CA_FILE"] ?? throw new InvalidOperationException("MQTT_CA_FILE is required."));
        var options = new MqttClientOptionsBuilder()
            .WithClientId(configuration["MQTT_CLIENT_ID"] ?? throw new InvalidOperationException("A stable MQTT_CLIENT_ID is required."))
            .WithTcpServer(configuration["MQTT_HOST"] ?? "localhost", configuration.GetValue("MQTT_PORT", 8883))
            .WithCredentials("florabot-backend", password)
            .WithProtocolVersion(MqttProtocolVersion.V500).WithCleanSession(false).WithSessionExpiryInterval(86400)
            .WithTimeout(TimeSpan.FromSeconds(5))
            .WithTlsOptions(tls => tls.UseTls().WithTrustChain(new X509Certificate2Collection(ca)))
            .Build();
        using var client = new MqttClientFactory().CreateMqttClient();
        var reconnect = 0;
        client.ApplicationMessageReceivedAsync += async args =>
        {
            args.AutoAcknowledge = false;
            try
            {
                var parts = args.ApplicationMessage.Topic.Split('/');
                if (!args.ApplicationMessage.Retain && args.ApplicationMessage.Payload.Length <= 4096
                    && args.ApplicationMessage.QualityOfServiceLevel != MqttQualityOfServiceLevel.AtMostOnce
                    && parts is ["kiosk", var hardware, "evt"] && DeviceProtocol.ValidHardwareId(hardware))
                {
                    SignedDeviceEvent? message = null;
                    try { message = JsonSerializer.Deserialize<SignedDeviceEvent>(args.ApplicationMessage.ConvertPayloadToString()); }
                    catch (JsonException) { }
                    if (message is not null)
                    {
                        using var scope = scopes.CreateScope();
                        var receipt = await scope.ServiceProvider.GetRequiredService<DeviceEventInbox>().ReceiveAsync(hardware, message, stoppingToken);
                        if (receipt == DeviceReceipt.Rejected) logger.LogWarning("Rejected MQTT event for device {Hardware}", hardware);
                    }
                }
                // Malformed/untrusted messages are terminal; valid messages are durable before PUBACK.
                await args.AcknowledgeAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "MQTT receipt failed; reconnecting without acknowledging delivery");
                Interlocked.Exchange(ref reconnect, 1);
            }
        };
        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    if (Interlocked.Exchange(ref reconnect, 0) == 1 && client.IsConnected)
                        await client.DisconnectAsync(new MqttClientDisconnectOptions(), stoppingToken);
                    if (!client.IsConnected)
                    {
                        await client.ConnectAsync(options, stoppingToken);
                        var subscription = await client.SubscribeAsync(new MqttClientSubscribeOptionsBuilder().WithTopicFilter(filter =>
                            filter.WithTopic("kiosk/+/evt").WithQualityOfServiceLevel(MqttQualityOfServiceLevel.AtLeastOnce)
                                .WithRetainAsPublished(true)).Build(), stoppingToken);
                        if (subscription.Items.Count != 1 || (int)subscription.Items.Single().ResultCode != 1)
                            throw new InvalidOperationException("MQTT event subscription did not grant QoS 1.");
                    }
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
                catch (Exception ex)
                {
                    Interlocked.Exchange(ref reconnect, 1);
                    logger.LogWarning(ex, "MQTT connection unavailable; durable inbox processing continues");
                }

                try
                {
                    using var scope = scopes.CreateScope();
                    var inbox = scope.ServiceProvider.GetRequiredService<DeviceEventInbox>();
                    foreach (var id in await inbox.PendingAsync(stoppingToken))
                    {
                        try { await inbox.ProcessAsync(id, stoppingToken); }
                        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { throw; }
                        catch (Exception ex) { logger.LogWarning(ex, "MQTT inbox processing failed for {EventId}; receipt retained", id); }
                    }
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
                catch (Exception ex) { logger.LogWarning(ex, "MQTT inbox unavailable; processing will retry"); }
                if (client.IsConnected && Volatile.Read(ref reconnect) == 0)
                {
                    try
                    {
                        using var scope = scopes.CreateScope();
                        var dispatcher = scope.ServiceProvider.GetRequiredService<DeviceCommandDispatcher>();
                        foreach (var id in await dispatcher.PendingAsync(stoppingToken))
                        {
                            var prepared = await dispatcher.PrepareAsync(id, stoppingToken);
                            if (prepared is null) continue;
                            await dispatcher.DeliverAsync(prepared, async (command, ct) =>
                            {
                                var result = await client.PublishAsync(new MqttApplicationMessageBuilder()
                                    .WithTopic($"kiosk/{command.HardwareId}/cmd")
                                    .WithPayload(JsonSerializer.Serialize(command)).WithRetainFlag(false)
                                    .WithQualityOfServiceLevel(MqttQualityOfServiceLevel.AtLeastOnce).Build(), ct);
                                if ((int)result.ReasonCode >= 128) throw new InvalidOperationException("MQTT command was rejected by the broker.");
                            }, stoppingToken);
                        }
                    }
                    catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
                    catch (Exception ex) { logger.LogWarning(ex, "MQTT command dispatch failed; persisted retry deadline retained"); }
                }
                await Task.Delay(TimeSpan.FromSeconds(2), stoppingToken);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
        finally
        {
            if (client.IsConnected) await client.DisconnectAsync(new MqttClientDisconnectOptions(), CancellationToken.None);
        }
    }
}
