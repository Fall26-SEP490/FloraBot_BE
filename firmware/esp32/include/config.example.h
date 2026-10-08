#pragma once
#include <array>
#include <cstdint>

namespace config {
struct Channel {
  uint8_t relayChannel;
  int relayPin;
  int sensorPin;
  bool relayActiveHigh;
  bool sensorClosedLow;
};
inline constexpr char hardwareId[] = "";
inline constexpr char wifiSsid[] = "";
inline constexpr char wifiPassword[] = "";
inline constexpr char mqttHost[] = "";
inline constexpr uint16_t mqttPort = 8883;
inline constexpr char mqttPassword[] = "";
inline constexpr char hmacKeyHex[] = "";
inline constexpr char caCertificate[] = "";
inline constexpr char ntpServer[] = "pool.ntp.org";
// Populate only after checking the actual board, relay driver and reed-switch wiring.
inline constexpr std::array<Channel, 0> channels{};
inline constexpr int recoveryButtonPin = -1;
inline constexpr uint32_t pulseMs = 500;
inline constexpr uint32_t sensorDebounceMs = 40;
inline constexpr uint32_t doorTimeoutMs = 60000;
}  // namespace config
