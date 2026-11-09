using System.Text;

namespace FloraBot.Api.Modules.KioskOps;

public static class Esp32ConfigurationHeader
{
    public static byte[] Render(Esp32Kiosk kiosk, Esp32BrokerConfiguration broker, Esp32DeviceSecrets secrets)
    {
        // Keep configuration symbols aligned with both IOT/FloraBot_IOT and firmware/esp32.
        // The hardware.h branch uses the established IOT pin table; the BE firmware fallback
        // deliberately leaves channels empty until its board wiring has been configured.
        var text = $$"""
            #pragma once
            #include <array>
            #include <cstdint>
            #if __has_include("hardware.h")
            #include "hardware.h"
            #endif

            // Private device configuration. Fill in local Wi-Fi before building; keep out of Git.
            // The firmware MQTT client ID is esp32- followed by hardwareId.
            namespace config {
            #if __has_include("hardware.h")
            using Channel = hardware::Channel;
            inline constexpr auto channels = hardware::channels;
            #else
            struct Channel {
              uint8_t relayChannel;
              int relayPin;
              int sensorPin;
              bool relayActiveHigh;
              bool sensorClosedLow;
            };
            // Configure board-specific channels from its established hardware template.
            inline constexpr std::array<Channel, 0> channels{};
            #endif
            inline constexpr char hardwareId[] = {{CppString(kiosk.HardwareId)}};
            inline constexpr char wifiSsid[] = "";
            inline constexpr char wifiPassword[] = "";
            inline constexpr char mqttHost[] = {{CppString(broker.Host)}};
            inline constexpr uint16_t mqttPort = {{broker.TlsPort}};
            inline constexpr char mqttPassword[] = {{CppString(secrets.MqttPassword)}};
            inline constexpr char hmacKeyHex[] = {{CppString(secrets.HmacKeyHex)}};
            inline constexpr char caCertificate[] = {{CppString(broker.CaCertificatePem)}};
            inline constexpr char ntpServer[] = "pool.ntp.org";
            inline constexpr int recoveryButtonPin = -1;
            inline constexpr uint32_t pulseMs = 500;
            inline constexpr uint32_t sensorDebounceMs = 40;
            inline constexpr uint32_t doorTimeoutMs = 60000;
            }  // namespace config

            """;
        return Encoding.UTF8.GetBytes(text);
    }

    public static string CppString(string value)
    {
        // Fixed-width octal escapes preserve exact UTF-8 bytes and cannot consume following
        // hex/octal characters, terminate a literal, inject directives, or form trigraphs.
        var result = new StringBuilder("\"");
        foreach (var b in Encoding.UTF8.GetBytes(value))
        {
            if (b is >= 32 and <= 126 && b is not (byte)'"' and not (byte)'\\' and not (byte)'?') result.Append((char)b);
            else result.Append('\\').Append(Convert.ToString(b, 8).PadLeft(3, '0'));
        }
        return result.Append('"').ToString();
    }
}
