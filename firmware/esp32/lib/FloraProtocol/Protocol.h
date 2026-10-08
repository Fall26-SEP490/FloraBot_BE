#pragma once

#include <array>
#include <cstdint>
#include <string>

namespace flora {
using Digest = std::array<uint8_t, 32>;
using Hmac = bool (*)(const Digest&, const std::string&, Digest&);

struct Command {
  int version = 1;
  std::string hardware;
  std::string id;
  std::string slot;
  int relay = -1;
  std::string purpose;
  int64_t issued = 0;
  int64_t expires = 0;
  std::string signature;
};

bool uuid(const std::string& value, bool allowZero = false);
bool hardwareId(const std::string& value);
bool decodeHex(const std::string& value, Digest& output);
std::string hex(const Digest& value);
bool canonical(const Command& command, std::string& output);
bool verify(const Command& command, const std::string& hardware, const Digest& key,
            int64_t now, Hmac hmac, Digest& identity);
bool eventCanonical(const std::string& hardware, const std::string& eventId,
                    const std::string& commandId, const std::string& event,
                    int64_t occurredAt, std::string& output);
}  // namespace flora
