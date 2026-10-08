#include "Protocol.h"

namespace flora {
namespace {
int nibble(char value) {
  if (value >= '0' && value <= '9') return value - '0';
  if (value >= 'a' && value <= 'f') return value - 'a' + 10;
  if (value >= 'A' && value <= 'F') return value - 'A' + 10;
  return -1;
}
std::string lower(std::string value) {
  for (char& c : value) if (c >= 'A' && c <= 'F') c += 'a' - 'A';
  return value;
}
}  // namespace

bool uuid(const std::string& value, bool allowZero) {
  if (value.size() != 36) return false;
  bool nonzero = false;
  for (size_t i = 0; i < value.size(); ++i) {
    if (i == 8 || i == 13 || i == 18 || i == 23) {
      if (value[i] != '-') return false;
    } else {
      if (nibble(value[i]) < 0) return false;
      nonzero |= value[i] != '0';
    }
  }
  return allowZero || nonzero;
}

bool hardwareId(const std::string& value) {
  if (value.empty() || value.size() > 64) return false;
  for (char c : value) {
    if (!((c >= 'A' && c <= 'Z') || (c >= 'a' && c <= 'z') ||
          (c >= '0' && c <= '9') || c == '-' || c == '_')) return false;
  }
  return true;
}

bool decodeHex(const std::string& value, Digest& output) {
  if (value.size() != 64) return false;
  for (size_t i = 0; i < output.size(); ++i) {
    const int a = nibble(value[i * 2]), b = nibble(value[i * 2 + 1]);
    if (a < 0 || b < 0) return false;
    output[i] = static_cast<uint8_t>((a << 4) | b);
  }
  return true;
}

std::string hex(const Digest& value) {
  constexpr char digits[] = "0123456789abcdef";
  std::string result;
  result.reserve(64);
  for (uint8_t byte : value) { result += digits[byte >> 4]; result += digits[byte & 15]; }
  return result;
}

bool canonical(const Command& c, std::string& output) {
  if (c.version != 1 || !hardwareId(c.hardware) || !uuid(c.id) || !uuid(c.slot) ||
      c.relay < 0 || c.relay > 63 || c.issued <= 0 || c.expires <= c.issued ||
      (c.purpose != "CUSTOMER_PICKUP" && c.purpose != "SELLER_ACCESS")) return false;
  output = "florabot.unlock.v1\n" + c.hardware + "\n" + lower(c.id) + "\n" + lower(c.slot) +
           "\n" + std::to_string(c.relay) + "\n" + c.purpose + "\n" +
           std::to_string(c.issued) + "\n" + std::to_string(c.expires);
  return true;
}

bool verify(const Command& command, const std::string& hardware, const Digest& key,
            int64_t now, Hmac hmac, Digest& identity) {
  std::string payload;
  Digest supplied{}, expected{};
  if (!hmac || command.hardware != hardware || now < command.issued || now >= command.expires ||
      !canonical(command, payload) || !decodeHex(command.signature, supplied) ||
      !hmac(key, payload, expected)) return false;
  uint8_t difference = 0;
  for (size_t i = 0; i < expected.size(); ++i) difference |= supplied[i] ^ expected[i];
  if (difference != 0) return false;
  // The verified HMAC binds every canonical field and is also the durable content identity.
  identity = expected;
  return true;
}

bool eventCanonical(const std::string& hardware, const std::string& eventId,
                    const std::string& commandId, const std::string& event,
                    int64_t occurredAt, std::string& output) {
  const bool heartbeat = event == "HEARTBEAT";
  const std::string zero = "00000000-0000-0000-0000-000000000000";
  if (!hardwareId(hardware) || !uuid(eventId) || !uuid(commandId, heartbeat) || occurredAt <= 0 ||
      (heartbeat && commandId != zero) ||
      (!heartbeat && event != "ACK" && event != "OPENED" && event != "CLOSED" && event != "FAILED")) return false;
  output = "florabot.event.v1\n" + hardware + "\n" + lower(eventId) + "\n" + lower(commandId) +
           "\n" + event + "\n" + std::to_string(occurredAt);
  return true;
}
}  // namespace flora
