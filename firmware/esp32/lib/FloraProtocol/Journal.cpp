#include "Journal.h"
#include <algorithm>
#include <cstring>
#include <memory>
#include <new>

namespace flora {
namespace {
std::string normalized(std::string id) {
  for (char& c : id) if (c >= 'A' && c <= 'F') c += 'a' - 'A';
  return id;
}
void copyId(std::array<char, 37>& destination, const std::string& id) {
  const auto lower = normalized(id);
  std::copy(lower.begin(), lower.end(), destination.begin());
  destination[36] = 0;
}
bool validId(const std::array<char, 37>& id) {
  return id[36] == 0 && uuid(std::string(id.data(), 36));
}
}  // namespace

bool Journal::valid(const Snapshot& value) {
  if (value.version != 1 || value.clockFloor < 0 || value.eventCount > value.events.size() ||
      value.active >= value.claims.size() || static_cast<unsigned>(value.phase) > 3) return false;
  for (size_t i = 0; i < value.claims.size(); ++i) {
    const auto& claim = value.claims[i];
    if (claim.expires == 0) continue;
    if (claim.expires < 0 || !validId(claim.id)) return false;
    for (size_t j = 0; j < i; ++j)
      if (value.claims[j].expires != 0 && value.claims[j].id == claim.id) return false;
  }
  if (value.phase != Phase::Idle && value.claims[value.active].expires == 0) return false;
  for (size_t i = 0; i < value.eventCount; ++i) {
    const auto& event = value.events[i];
    if (!validId(event.id) || !validId(event.command) || event.at <= 0 ||
        event.at > value.clockFloor || static_cast<unsigned>(event.kind) > 2) return false;
    for (size_t j = 0; j < i; ++j) if (value.events[j].id == event.id) return false;
  }
  return true;
}

Journal::Journal(const Snapshot& saved, Commit commit)
    : state_(saved), commit_(std::move(commit)), healthy_(valid(saved)),
      recovery_(saved.phase != Phase::Idle) {}

bool Journal::save(const Snapshot& candidate) {
  if (!healthy_ || !valid(candidate) || !commit_ || !commit_(candidate)) {
    healthy_ = false;
    return false;
  }
  state_ = candidate;
  return true;
}

ClaimResult Journal::claim(const Command& command, const Digest& verifiedIdentity,
                           const std::string& ackId, int64_t now) {
  std::string canonicalPayload;
  if (!healthy_) return ClaimResult::StorageError;
  if (!canonical(command, canonicalPayload) || !uuid(ackId) || now < state_.clockFloor ||
      now < command.issued || now >= command.expires) return ClaimResult::Rejected;
  const auto id = normalized(command.id);
  for (const auto& claim : state_.claims) {
    if (claim.expires != 0 && id == claim.id.data())
      return claim.identity == verifiedIdentity ? ClaimResult::Duplicate : ClaimResult::Conflict;
  }
  if (recovery_ || state_.phase != Phase::Idle) return ClaimResult::Busy;
  // Reserve room for the entire ACK -> OPENED -> CLOSED sequence before any pulse.
  if (state_.eventCount > state_.events.size() - 3) return ClaimResult::Full;
  size_t index = state_.claims.size();
  for (size_t i = 0; i < state_.claims.size(); ++i) {
    if (state_.claims[i].expires <= now) { index = i; break; }
  }
  if (index == state_.claims.size()) return ClaimResult::Full;
  auto next = std::unique_ptr<Snapshot>(new (std::nothrow) Snapshot(state_));
  if (!next) return ClaimResult::StorageError;
  next->clockFloor = now;
  copyId(next->claims[index].id, command.id);
  next->claims[index].identity = verifiedIdentity;
  next->claims[index].expires = command.expires;
  next->active = static_cast<uint8_t>(index);
  next->phase = Phase::Prepared;
  auto& event = next->events[next->eventCount++];
  copyId(event.id, ackId); copyId(event.command, command.id);
  event.at = now; event.kind = EventKind::Ack;
  return save(*next) ? ClaimResult::Accepted : ClaimResult::StorageError;
}

bool Journal::beginPulse(int64_t now) {
  if (!healthy_ || recovery_ || state_.phase != Phase::Prepared || now < state_.clockFloor ||
      now >= state_.claims[state_.active].expires) return false;
  auto next = std::unique_ptr<Snapshot>(new (std::nothrow) Snapshot(state_));
  if (!next) return false;
  next->phase = Phase::WaitingOpen; next->clockFloor = now;
  return save(*next);
}

bool Journal::transition(EventKind kind, Phase expected, Phase nextPhase, const std::string& id, int64_t now) {
  if (!healthy_ || recovery_ || state_.phase != expected || !uuid(id) ||
      now < state_.clockFloor || state_.eventCount >= state_.events.size()) return false;
  auto next = std::unique_ptr<Snapshot>(new (std::nothrow) Snapshot(state_));
  if (!next) return false;
  auto& event = next->events[next->eventCount++];
  copyId(event.id, id); event.command = state_.claims[state_.active].id;
  event.kind = kind; event.at = now;
  next->phase = nextPhase; next->clockFloor = now;
  return save(*next);
}

bool Journal::opened(const std::string& id, int64_t now) {
  return transition(EventKind::Opened, Phase::WaitingOpen, Phase::WaitingClose, id, now);
}
bool Journal::closed(const std::string& id, int64_t now) {
  return transition(EventKind::Closed, Phase::WaitingClose, Phase::Idle, id, now);
}

bool Journal::acknowledge(const std::string& eventId) {
  if (!healthy_ || state_.eventCount == 0 || normalized(eventId) != state_.events[0].id.data()) return false;
  auto next = std::unique_ptr<Snapshot>(new (std::nothrow) Snapshot(state_));
  if (!next) return false;
  std::move(next->events.begin() + 1, next->events.begin() + next->eventCount, next->events.begin());
  next->events[--next->eventCount] = Event{};
  return save(*next);
}

bool Journal::clearRecoveryAfterInspection(bool sensorClosed) {
  if (!healthy_ || !recovery_ || !sensorClosed) return false;
  auto next = std::unique_ptr<Snapshot>(new (std::nothrow) Snapshot(state_));
  if (!next) return false;
  next->phase = Phase::Idle;
  if (!save(*next)) return false;
  recovery_ = false;
  return true;
}
}  // namespace flora
