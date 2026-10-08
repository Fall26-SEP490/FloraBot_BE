#pragma once

#include "Protocol.h"
#include <array>
#include <functional>

namespace flora {
enum class Phase : uint8_t { Idle, Prepared, WaitingOpen, WaitingClose };
enum class EventKind : uint8_t { Ack, Opened, Closed };
enum class ClaimResult { Accepted, Duplicate, Conflict, Busy, Full, Rejected, StorageError };
struct Claim {
  std::array<char, 37> id{};
  Digest identity{};
  int64_t expires = 0;
};
struct Event {
  std::array<char, 37> id{};
  std::array<char, 37> command{};
  int64_t at = 0;
  EventKind kind = EventKind::Ack;
};
struct Snapshot {
  uint32_t version = 1;
  int64_t clockFloor = 0;
  std::array<Claim, 32> claims{};
  std::array<Event, 16> events{};
  uint8_t eventCount = 0;
  uint8_t active = 0;
  Phase phase = Phase::Idle;
};

// Commit must durably replace the entire snapshot before returning true (one NVS blob).
// All methods are called under the same application mutex; networking never owns that mutex.
class Journal {
 public:
  using Commit = std::function<bool(const Snapshot&)>;
  Journal(const Snapshot& saved, Commit commit);
  bool healthy() const { return healthy_; }
  bool recoveryRequired() const { return recovery_; }
  void requireInspection() { recovery_ = true; }
  const Snapshot& snapshot() const { return state_; }
  ClaimResult claim(const Command& command, const Digest& verifiedIdentity,
                    const std::string& ackId, int64_t now);
  bool beginPulse(int64_t now);
  bool opened(const std::string& eventId, int64_t now);
  bool closed(const std::string& eventId, int64_t now);
  bool acknowledge(const std::string& eventId);
  bool clearRecoveryAfterInspection(bool sensorClosed);
  static bool valid(const Snapshot& value);

 private:
  bool save(const Snapshot& candidate);
  bool transition(EventKind kind, Phase expected, Phase next, const std::string& id, int64_t now);
  Snapshot state_;
  Commit commit_;
  bool healthy_;
  bool recovery_;
};
}  // namespace flora
