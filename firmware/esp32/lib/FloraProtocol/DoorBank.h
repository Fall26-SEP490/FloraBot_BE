#pragma once
#include "Journal.h"
#include <memory>
#include <vector>

namespace flora {
class DoorBank {
 public:
  bool add(uint8_t relay, const Snapshot& saved, Journal::Commit commit);
  Journal* door(uint8_t relay);
  bool healthy() const;
  ClaimResult claim(const Command& command, const Digest& verifiedIdentity, const std::string& ackId, int64_t now);
  bool nextEvent(size_t& cursor, Event& event, uint8_t& relay) const;
  bool acknowledge(uint8_t relay, const std::string& eventId);

 private:
  struct Door { uint8_t relay; std::unique_ptr<Journal> journal; };
  std::vector<Door> doors_;
};
}  // namespace flora
