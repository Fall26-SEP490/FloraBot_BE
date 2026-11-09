using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text.Json;

namespace FloraBot.Api.Modules.KioskOps;

public sealed class DeviceKeyRing
{
    private readonly ConcurrentDictionary<string, byte[]> keys;
    private readonly HashSet<string> legacyHardware;
    private readonly HashSet<string> dynamicHardware = new(StringComparer.Ordinal);
    private readonly object mutationLock = new();
    public string[] HardwareIds => keys.Keys.ToArray();

    public DeviceKeyRing(IReadOnlyDictionary<string, byte[]> source)
    {
        if (source.Any(pair => !DeviceProtocol.ValidHardwareId(pair.Key) || pair.Value.Length != 32))
            throw new ArgumentException("Device identities and 32-byte keys are required.", nameof(source));
        keys = new(source.Select(pair => new KeyValuePair<string, byte[]>(pair.Key, pair.Value.ToArray())), StringComparer.Ordinal);
        legacyHardware = new(source.Keys, StringComparer.Ordinal);
    }

    public static DeviceKeyRing Load(string file, bool allowEmpty = false)
    {
        using var json = JsonDocument.Parse(File.ReadAllText(file));
        var keys = new Dictionary<string, byte[]>();
        foreach (var entry in json.RootElement.EnumerateObject())
            if (entry.Value.TryGetProperty("hmacKey", out var key)) keys.Add(entry.Name, Convert.FromHexString(key.GetString()!));
        if (keys.Count == 0 && !allowEmpty) throw new InvalidOperationException("No device signing keys configured.");
        return new(keys);
    }

    /// <summary>Adds a newly provisioned key or verifies the existing key without rotating it.</summary>
    public bool AddOrVerify(string hardware, byte[] candidate)
    {
        if (!DeviceProtocol.ValidHardwareId(hardware) || candidate.Length != 32)
            throw new ArgumentException("A valid device identity and 32-byte key are required.");
        lock (mutationLock)
        {
            if (keys.TryGetValue(hardware, out var actual))
                return CryptographicOperations.FixedTimeEquals(actual, candidate);
            keys[hardware] = candidate.ToArray();
            dynamicHardware.Add(hardware);
            return true;
        }
    }

    public void RemoveDynamic(string hardware)
    {
        lock (mutationLock)
        {
            if (!legacyHardware.Contains(hardware) && dynamicHardware.Remove(hardware))
                keys.TryRemove(hardware, out _);
        }
    }

    /// <summary>Refreshes durable READY keys and removes deleted dynamic devices without changing legacy file keys.</summary>
    public string[] ReplaceDynamic(IReadOnlyDictionary<string, byte[]> snapshot)
    {
        if (snapshot.Any(pair => !DeviceProtocol.ValidHardwareId(pair.Key) || pair.Value.Length != 32))
            throw new ArgumentException("Device identities and 32-byte keys are required.", nameof(snapshot));
        lock (mutationLock)
        {
            foreach (var hardware in dynamicHardware.Where(h => !snapshot.ContainsKey(h)).ToArray())
            {
                keys.TryRemove(hardware, out _);
                dynamicHardware.Remove(hardware);
            }
            var conflicts = new List<string>();
            foreach (var (hardware, candidate) in snapshot)
            {
                if (keys.TryGetValue(hardware, out var actual))
                {
                    if (!CryptographicOperations.FixedTimeEquals(actual, candidate))
                    {
                        conflicts.Add(hardware);
                        if (!legacyHardware.Contains(hardware))
                        {
                            keys.TryRemove(hardware, out _);
                            dynamicHardware.Remove(hardware);
                        }
                    }
                    continue;
                }
                keys[hardware] = candidate.ToArray();
                dynamicHardware.Add(hardware);
            }
            return conflicts.ToArray();
        }
    }

    public bool Verify(SignedDeviceEvent message, string hardware, DateTimeOffset now) =>
        keys.TryGetValue(hardware, out var key) && DeviceProtocol.Verify(message, hardware, key, now);

    public bool Verify(SignedFlowerPresence message, string hardware, DateTimeOffset now) =>
        keys.TryGetValue(hardware, out var key) && FlowerPresenceProtocol.Verify(message, hardware, key, now);

    public bool Verify(UnlockCommand message, DateTimeOffset now) =>
        keys.TryGetValue(message.HardwareId, out var key) && DeviceProtocol.Verify(message, message.HardwareId, key, now);

    public bool Verify(UnlockRetryCommand message, DateTimeOffset now) =>
        keys.TryGetValue(message.HardwareId, out var key) && DeviceRetryProtocol.Verify(message, message.HardwareId, key, now);

    public bool Verify(UnlockRetryCommand message, string hardware, DateTimeOffset now) =>
        keys.TryGetValue(hardware, out var key) && DeviceRetryProtocol.Verify(message, hardware, key, now);

    public UnlockCommand Sign(UnlockCommand command) => keys.TryGetValue(command.HardwareId, out var key)
        ? DeviceProtocol.Sign(command, key) : throw new InvalidOperationException("Device has no signing key.");

    public UnlockRetryCommand Sign(UnlockRetryCommand command) => keys.TryGetValue(command.HardwareId, out var key)
        ? DeviceRetryProtocol.Sign(command, key) : throw new InvalidOperationException("Device has no signing key.");
}
