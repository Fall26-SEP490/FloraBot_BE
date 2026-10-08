using System.Text.Json;

namespace FloraBot.Api.Modules.KioskOps;

public sealed class DeviceKeyRing
{
    private readonly Dictionary<string, byte[]> keys;
    public string[] HardwareIds => keys.Keys.ToArray();

    public DeviceKeyRing(IReadOnlyDictionary<string, byte[]> source)
    {
        if (source.Any(pair => !DeviceProtocol.ValidHardwareId(pair.Key) || pair.Value.Length != 32))
            throw new ArgumentException("Device identities and 32-byte keys are required.", nameof(source));
        keys = source.ToDictionary(pair => pair.Key, pair => pair.Value.ToArray(), StringComparer.Ordinal);
    }

    public static DeviceKeyRing Load(string file)
    {
        using var json = JsonDocument.Parse(File.ReadAllText(file));
        var keys = new Dictionary<string, byte[]>();
        foreach (var entry in json.RootElement.EnumerateObject())
            if (entry.Value.TryGetProperty("hmacKey", out var key)) keys.Add(entry.Name, Convert.FromHexString(key.GetString()!));
        if (keys.Count == 0) throw new InvalidOperationException("No device signing keys configured.");
        return new(keys);
    }

    public bool Verify(SignedDeviceEvent message, string hardware, DateTimeOffset now) =>
        keys.TryGetValue(hardware, out var key) && DeviceProtocol.Verify(message, hardware, key, now);

    public bool Verify(UnlockCommand message, DateTimeOffset now) =>
        keys.TryGetValue(message.HardwareId, out var key) && DeviceProtocol.Verify(message, message.HardwareId, key, now);

    public UnlockCommand Sign(UnlockCommand command) => keys.TryGetValue(command.HardwareId, out var key)
        ? DeviceProtocol.Sign(command, key) : throw new InvalidOperationException("Device has no signing key.");
}
