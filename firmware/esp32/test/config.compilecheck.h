#pragma once
#include <array>
#include <cstdint>
// Non-operational fixture, only for linking every hardware path. Never flash this target.
namespace config {
struct Channel { uint8_t relayChannel; int relayPin; int sensorPin; bool relayActiveHigh; bool sensorClosedLow; };
inline constexpr char hardwareId[] = "ESP32-COMPILE-CHECK";
inline constexpr char wifiSsid[] = "compile-check-only";
inline constexpr char wifiPassword[] = "not-a-real-password";
inline constexpr char mqttHost[] = "broker.invalid";
inline constexpr uint16_t mqttPort = 8883;
inline constexpr char mqttPassword[] = "not-a-real-password";
inline constexpr char hmacKeyHex[] = "0000000000000000000000000000000000000000000000000000000000000000";
inline constexpr char caCertificate[] = "-----BEGIN CERTIFICATE-----\nINVALID-COMPILE-FIXTURE\n-----END CERTIFICATE-----";
inline constexpr char ntpServer[] = "pool.ntp.org";
inline constexpr std::array<Channel, 2> channels{{{0, 26, 27, true, true}, {1, 32, 33, true, true}}};
inline constexpr int recoveryButtonPin = 25;
inline constexpr uint32_t pulseMs = 500;
inline constexpr uint32_t sensorDebounceMs = 40;
inline constexpr uint32_t doorTimeoutMs = 60000;
}  // namespace config
