#include "DoorBank.h"
#include <new>

namespace flora {
bool DoorBank::add(uint8_t relay, const Snapshot& saved, Journal::Commit commit) {
  if (relay > 63 || door(relay) || !Journal::valid(saved)) return false;
  auto journal = std::unique_ptr<Journal>(new (std::nothrow) Journal(saved, std::move(commit)));
  if (!journal) return false;
  doors_.push_back({relay, std::move(journal)});
  return true;
}

Journal* DoorBank::door(uint8_t relay) {
  for (auto& entry : doors_) if (entry.relay == relay) return entry.journal.get();
  return nullptr;
}

bool DoorBank::healthy() const {
  if (doors_.empty()) return false;
  for (const auto& entry : doors_) if (!entry.journal->healthy()) return false;
  return true;
}

ClaimResult DoorBank::claim(const Command& command, const Digest& identity, const std::string& ackId, int64_t now) {
  if (!healthy()) return ClaimResult::StorageError;
  if (command.relay < 0 || command.relay > 63) return ClaimResult::Rejected;
  auto* target = door(static_cast<uint8_t>(command.relay));
  if (!target) return ClaimResult::Rejected;
  std::string id = command.id;
  for (char& c : id) if (c >= 'A' && c <= 'F') c += 'a' - 'A';
  // Identity and the clock floor apply to the entire device, not just one physical relay.
  for (const auto& entry : doors_) {
    const auto& saved = entry.journal->snapshot();
    if (now < saved.clockFloor) return ClaimResult::Rejected;
    for (const auto& claim : saved.claims) {
      if (claim.expires && id == claim.id.data())
        return entry.relay == command.relay && claim.identity == identity ? ClaimResult::Duplicate : ClaimResult::Conflict;
    }
  }
  return target->claim(command, identity, ackId, now);
}

bool DoorBank::nextEvent(size_t& cursor, Event& event, uint8_t& relay) const {
  if (!healthy()) return false;
  for (size_t offset = 0; offset < doors_.size(); ++offset) {
    const size_t index = (cursor + offset) % doors_.size();
    const auto& snapshot = doors_[index].journal->snapshot();
    if (snapshot.eventCount) {
      event = snapshot.events[0]; relay = doors_[index].relay;
      cursor = (index + 1) % doors_.size(); return true;
    }
  }
  return false;
}

bool DoorBank::acknowledge(uint8_t relay, const std::string& eventId) {
  auto* target = door(relay);
  return target && target->acknowledge(eventId);
}
}  // namespace flora
