#include "Storage.h"
#include <limits>

namespace flora {
namespace {
uint32_t crc(const uint8_t* bytes, size_t size) {
  uint32_t value = 0xffffffff;
  for (size_t i = 0; i < size; ++i) {
    value ^= bytes[i];
    for (int bit = 0; bit < 8; ++bit) value = (value >> 1) ^ (0xedb88320 & (0u - (value & 1)));
  }
  return ~value;
}
void integer(std::vector<uint8_t>& output, uint64_t value, unsigned count) {
  for (unsigned i = count; i > 0; --i) output.push_back(value >> ((i - 1) * 8));
}
template<class T> void bytes(std::vector<uint8_t>& output, const T& value) {
  output.insert(output.end(), value.begin(), value.end());
}
struct Reader {
  const uint8_t* data;
  size_t size;
  size_t position = 0;
  bool ok = true;
  uint64_t number(unsigned count) {
    if (count > size - position) { ok = false; return 0; }
    uint64_t result = 0;
    while (count--) result = (result << 8) | data[position++];
    return result;
  }
  int64_t timestamp() {
    const auto result = number(8);
    if (result > static_cast<uint64_t>(std::numeric_limits<int64_t>::max())) ok = false;
    return static_cast<int64_t>(result);
  }
  template<class T> void copy(T& value) {
    if (value.size() > size - position) { ok = false; return; }
    for (auto& item : value) item = data[position++];
  }
};
}  // namespace

std::vector<uint8_t> encodeSnapshot(const Snapshot& snapshot) {
  if (!Journal::valid(snapshot)) return {};
  std::vector<uint8_t> output{'F', 'L', 'R', 'J'};
  output.reserve(4096);
  integer(output, snapshot.version, 4); integer(output, snapshot.clockFloor, 8);
  integer(output, snapshot.eventCount, 1); integer(output, snapshot.active, 1);
  integer(output, static_cast<uint8_t>(snapshot.phase), 1);
  for (const auto& claim : snapshot.claims) {
    bytes(output, claim.id); bytes(output, claim.identity); integer(output, claim.expires, 8);
  }
  for (const auto& event : snapshot.events) {
    bytes(output, event.id); bytes(output, event.command); integer(output, event.at, 8);
    integer(output, static_cast<uint8_t>(event.kind), 1);
  }
  integer(output, crc(output.data(), output.size()), 4);
  return output;
}

bool decodeSnapshot(const uint8_t* data, size_t size, Snapshot& snapshot) {
  constexpr size_t encodedSize = 19 + 32 * (37 + 32 + 8) + 16 * (37 + 37 + 8 + 1) + 4;
  if (!data || size != encodedSize || data[0] != 'F' || data[1] != 'L' || data[2] != 'R' || data[3] != 'J') return false;
  Reader checksum{data + size - 4, 4};
  if (checksum.number(4) != crc(data, size - 4)) return false;
  Snapshot candidate{};
  Reader reader{data, size - 4, 4};
  candidate.version = reader.number(4); candidate.clockFloor = reader.timestamp();
  candidate.eventCount = reader.number(1); candidate.active = reader.number(1);
  candidate.phase = static_cast<Phase>(reader.number(1));
  for (auto& claim : candidate.claims) {
    reader.copy(claim.id); reader.copy(claim.identity); claim.expires = reader.timestamp();
  }
  for (auto& event : candidate.events) {
    reader.copy(event.id); reader.copy(event.command); event.at = reader.timestamp();
    event.kind = static_cast<EventKind>(reader.number(1));
  }
  if (!reader.ok || reader.position != size - 4 || !Journal::valid(candidate)) return false;
  snapshot = candidate;
  return true;
}
}  // namespace flora
