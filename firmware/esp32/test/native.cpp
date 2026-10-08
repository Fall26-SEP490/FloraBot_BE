#include "Journal.h"
#include "Storage.h"
#include "DoorBank.h"
#include <openssl/hmac.h>
#ifdef FLORA_MBEDTLS
#include "MbedHmac.h"
#endif
#include <cstdio>
#include <iostream>
#include <stdexcept>
#include <vector>

#define CHECK(condition) do { if (!(condition)) throw std::runtime_error("Check failed at line " + std::to_string(__LINE__) + ": " #condition); } while (false)

using namespace flora;
constexpr int64_t now = 1800000000;
Digest key() { Digest result{}; for (size_t i = 0; i < result.size(); ++i) result[i] = i; return result; }
bool hmac(const Digest& key, const std::string& message, Digest& result) {
#ifdef FLORA_MBEDTLS
  return mbedHmac(key, message, result);
#else
  unsigned size = 0;
  return HMAC(EVP_sha256(), key.data(), key.size(), reinterpret_cast<const uint8_t*>(message.data()),
              message.size(), result.data(), &size) && size == result.size();
#endif
}
std::string id(unsigned number) {
  char value[37];
  std::snprintf(value, sizeof(value), "00000000-0000-4000-8000-%012u", number);
  return value;
}
Command command() {
  return {1, "ESP32-A1B2C3", "11111111-1111-1111-1111-111111111111",
          "22222222-2222-2222-2222-222222222222", 7, "CUSTOMER_PICKUP", now, now + 60,
          "c8795c2a6674c0de6c04d8823b5d53b5e606a493e358a3fffa14c475b6b20d29"};
}
Digest signedIdentity(Command& c) {
  std::string payload; Digest result{};
  CHECK(canonical(c, payload)); CHECK(hmac(key(), payload, result)); c.signature = hex(result);
  return result;
}
struct Storage {
  Snapshot saved{};
  bool fail = false;
  unsigned writes = 0;
  Journal open() { return Journal(saved, [this](const Snapshot& candidate) {
    ++writes; if (fail) return false; saved = candidate; return true;
  }); }
};

void vectorAndFields() {
  auto c = command(); Digest identity{};
  CHECK(verify(c, c.hardware, key(), now, hmac, identity)); CHECK(hex(identity) == c.signature);
  for (unsigned field = 0; field < 9; ++field) {
    auto changed = c;
    switch (field) {
      case 0: changed.version = 2; break;
      case 1: changed.hardware = "FOREIGN"; break;
      case 2: changed.id = id(99); break;
      case 3: changed.slot = id(100); break;
      case 4: changed.relay++; break;
      case 5: changed.purpose = "SELLER_ACCESS"; break;
      case 6: changed.issued--; break;
      case 7: changed.expires++; break;
      case 8: changed.signature[0] = '0'; break;
    }
    CHECK(!verify(changed, c.hardware, key(), now, hmac, identity));
  }
  auto wrongKey = key(); wrongKey[0] ^= 1;
  CHECK(!verify(c, c.hardware, wrongKey, now, hmac, identity));
  CHECK(!verify(c, c.hardware, key(), now - 1, hmac, identity));
  CHECK(!verify(c, c.hardware, key(), now + 60, hmac, identity));
  CHECK(!verify(c, c.hardware, key(), 0, hmac, identity));
}

void canonicalValidation() {
  auto c = command(); std::string payload;
  for (const auto& invalid : {"", "../device", "device\nfield", "device/other"}) CHECK(!hardwareId(invalid));
  CHECK(!hardwareId(std::string(65, 'a')));
  CHECK(!uuid("00000000-0000-0000-0000-000000000000"));
  c.relay = 64; CHECK(!canonical(c, payload)); c = command();
  c.expires = c.issued; CHECK(!canonical(c, payload)); c = command();
  c.purpose = "OTHER"; CHECK(!canonical(c, payload)); c = command();
  c.id = "ABCDEFAB-1111-1111-1111-111111111111"; CHECK(canonical(c, payload));
  CHECK(payload.find("abcdefab-1111") != std::string::npos);
  CHECK(payload.back() != '\n');
  Digest bytes{}; CHECK(!decodeHex(std::string(64, 'g'), bytes));
  CHECK(!decodeHex(std::string(63, '0'), bytes));
}

void eventDomain() {
  std::string payload;
  CHECK(eventCanonical("ESP32-A1B2C3", id(1), command().id, "ACK", now, payload));
  CHECK(payload.rfind("florabot.event.v1\n", 0) == 0);
  CHECK(!eventCanonical("ESP32-A1B2C3", id(1), command().id, "SENT", now, payload));
  CHECK(!eventCanonical("ESP32-A1B2C3", id(1), command().id, "HEARTBEAT", now, payload));
  const std::string zero = "00000000-0000-0000-0000-000000000000";
  CHECK(eventCanonical("ESP32-A1B2C3", id(1), zero, "HEARTBEAT", now, payload));
  CHECK(!eventCanonical("ESP32-A1B2C3", zero, command().id, "ACK", now, payload));
  CHECK(!eventCanonical("ESP32-A1B2C3", id(1), zero, "ACK", now, payload));
}

void claimBeforePulseAndReplay() {
  Storage storage; auto journal = storage.open(); auto c = command(); auto digest = signedIdentity(c);
  CHECK(journal.claim(c, digest, id(1), now) == ClaimResult::Accepted);
  CHECK(storage.saved.phase == Phase::Prepared && storage.saved.eventCount == 1);
  CHECK(journal.beginPulse(now)); CHECK(storage.saved.phase == Phase::WaitingOpen);
  CHECK(!journal.beginPulse(now));
  CHECK(journal.claim(c, digest, id(2), now) == ClaimResult::Duplicate);
  c.relay++; auto changed = signedIdentity(c);
  CHECK(journal.claim(c, changed, id(2), now) == ClaimResult::Conflict);
}

void crashWindows() {
  for (unsigned phase = 0; phase < 3; ++phase) {
    Storage storage; auto journal = storage.open(); auto c = command(); auto digest = signedIdentity(c);
    CHECK(journal.claim(c, digest, id(1), now) == ClaimResult::Accepted);
    if (phase >= 1) CHECK(journal.beginPulse(now));
    if (phase >= 2) CHECK(journal.opened(id(2), now + 1));
    auto reboot = storage.open();
    CHECK(reboot.recoveryRequired()); CHECK(!reboot.beginPulse(now + 2));
    CHECK(reboot.claim(c, digest, id(3), now + 2) == ClaimResult::Duplicate);
    auto fresh = command(); fresh.id = id(20); auto newDigest = signedIdentity(fresh);
    CHECK(reboot.claim(fresh, newDigest, id(3), now + 2) == ClaimResult::Busy);
    CHECK(!reboot.closed(id(3), now + 2));
    CHECK(!reboot.clearRecoveryAfterInspection(false));
    CHECK(reboot.clearRecoveryAfterInspection(true));
    CHECK(reboot.claim(c, digest, id(3), now + 2) == ClaimResult::Duplicate);
  }
}

void storageFailure() {
  Storage storage; auto journal = storage.open(); auto c = command(); auto digest = signedIdentity(c);
  storage.fail = true;
  CHECK(journal.claim(c, digest, id(1), now) == ClaimResult::StorageError);
  CHECK(!journal.healthy() && !journal.beginPulse(now)); CHECK(storage.saved.phase == Phase::Idle);
  storage.fail = false; auto retry = storage.open();
  CHECK(retry.claim(c, digest, id(1), now) == ClaimResult::Accepted);
  storage.fail = true; CHECK(!retry.beginPulse(now)); CHECK(storage.saved.phase == Phase::Prepared);
  CHECK(!retry.healthy()); CHECK(storage.open().recoveryRequired());
}

void sensorOrderAndDurableEvents() {
  Storage storage; auto journal = storage.open(); auto c = command(); auto digest = signedIdentity(c);
  CHECK(journal.claim(c, digest, id(1), now) == ClaimResult::Accepted);
  CHECK(!journal.closed(id(3), now)); CHECK(!journal.opened(id(2), now));
  CHECK(journal.beginPulse(now)); CHECK(!journal.closed(id(3), now));
  CHECK(journal.opened(id(2), now + 1)); CHECK(!journal.opened(id(4), now + 1));
  CHECK(journal.closed(id(3), now + 2)); CHECK(!journal.closed(id(4), now + 2));
  CHECK(storage.saved.eventCount == 3);
  auto reboot = storage.open(); CHECK(!reboot.recoveryRequired());
  CHECK(!reboot.acknowledge(id(2))); CHECK(reboot.acknowledge(id(1)));
  CHECK(storage.saved.events[0].kind == EventKind::Opened);
  storage.fail = true; CHECK(!reboot.acknowledge(id(2))); CHECK(storage.saved.eventCount == 2);
  storage.fail = false; auto again = storage.open();
  CHECK(again.acknowledge(id(2))); CHECK(again.acknowledge(id(3))); CHECK(storage.saved.eventCount == 0);
}

void pendingQueueCapacity() {
  Storage storage; auto journal = storage.open();
  for (unsigned i = 0; i < 5; ++i) {
    auto c = command(); c.id = id(i + 1); auto digest = signedIdentity(c);
    CHECK(journal.claim(c, digest, id(100 + 3 * i), now) == ClaimResult::Accepted);
    CHECK(journal.beginPulse(now)); CHECK(journal.opened(id(101 + 3 * i), now));
    CHECK(journal.closed(id(102 + 3 * i), now));
  }
  auto c = command(); auto digest = signedIdentity(c);
  CHECK(journal.claim(c, digest, id(999), now) == ClaimResult::Full);
  CHECK(storage.saved.eventCount == 15 && storage.saved.phase == Phase::Idle);
}

void claimsCapacityAndClockRollback() {
  Storage storage; auto journal = storage.open();
  for (unsigned i = 0; i < 32; ++i) {
    auto c = command(); c.id = id(i + 1); auto digest = signedIdentity(c);
    CHECK(journal.claim(c, digest, id(100 + 3 * i), now) == ClaimResult::Accepted);
    CHECK(journal.beginPulse(now)); CHECK(journal.opened(id(101 + 3 * i), now));
    CHECK(journal.closed(id(102 + 3 * i), now));
    CHECK(journal.acknowledge(id(100 + 3 * i))); CHECK(journal.acknowledge(id(101 + 3 * i)));
    CHECK(journal.acknowledge(id(102 + 3 * i)));
  }
  auto c = command(); auto digest = signedIdentity(c);
  CHECK(journal.claim(c, digest, id(999), now) == ClaimResult::Full);
  c.issued = now + 60; c.expires = now + 120; digest = signedIdentity(c);
  CHECK(journal.claim(c, digest, id(999), now + 60) == ClaimResult::Accepted);
  auto reboot = storage.open(); CHECK(reboot.clearRecoveryAfterInspection(true));
  auto old = command(); old.id = id(1); auto oldDigest = signedIdentity(old);
  CHECK(reboot.claim(old, oldDigest, id(998), now + 1) == ClaimResult::Rejected);
}

void corruptedStateAndExpiry() {
  Snapshot invalid{}; invalid.eventCount = 17; CHECK(!Journal::valid(invalid));
  invalid = {}; invalid.phase = Phase::Prepared; CHECK(!Journal::valid(invalid));
  invalid = {}; invalid.version = 2; CHECK(!Journal::valid(invalid));
  Journal broken(invalid, [](const Snapshot&) { return true; }); CHECK(!broken.healthy());
  Storage storage; auto journal = storage.open(); auto c = command(); auto digest = signedIdentity(c);
  CHECK(journal.claim(c, digest, id(1), now) == ClaimResult::Accepted);
  CHECK(!journal.beginPulse(now + 60)); CHECK(!journal.beginPulse(now - 1));
}

void storageEncoding() {
  Storage storage; auto journal = storage.open(); auto c = command(); auto digest = signedIdentity(c);
  CHECK(journal.claim(c, digest, id(1), now) == ClaimResult::Accepted);
  CHECK(journal.beginPulse(now)); CHECK(journal.opened(id(2), now + 1));
  const auto encoded = encodeSnapshot(storage.saved);
  CHECK(encoded.size() < 4096);
  Snapshot restored{};
  CHECK(decodeSnapshot(encoded.data(), encoded.size(), restored));
  CHECK(restored.phase == Phase::WaitingClose && restored.eventCount == 2);
  CHECK(restored.claims[restored.active].identity == digest);
  CHECK(restored.events[0].id == storage.saved.events[0].id);
  CHECK(encodeSnapshot(restored) == encoded);
  for (size_t offset = 0; offset < encoded.size(); ++offset) {
    auto corrupted = encoded; corrupted[offset] ^= 1;
    CHECK(!decodeSnapshot(corrupted.data(), corrupted.size(), restored));
  }
  CHECK(!decodeSnapshot(encoded.data(), encoded.size() - 1, restored));
  CHECK(!decodeSnapshot(nullptr, 0, restored));
}

void parallelDoorsAndGlobalReplay() {
  Storage first, second; DoorBank bank;
  CHECK(bank.add(7, first.saved, [&](const Snapshot& s) { first.saved = s; return true; }));
  CHECK(bank.add(8, second.saved, [&](const Snapshot& s) { second.saved = s; return true; }));
  CHECK(!bank.add(7, {}, [](const Snapshot&) { return true; }));
  auto a = command(); auto digestA = signedIdentity(a);
  auto b = command(); b.id = id(20); b.relay = 8; auto digestB = signedIdentity(b);
  CHECK(bank.claim(a, digestA, id(1), now) == ClaimResult::Accepted);
  CHECK(bank.claim(b, digestB, id(4), now) == ClaimResult::Accepted);
  CHECK(bank.door(7)->beginPulse(now)); CHECK(bank.door(8)->beginPulse(now));
  CHECK(bank.door(7)->opened(id(2), now)); CHECK(bank.door(8)->opened(id(5), now));
  CHECK(bank.door(8)->closed(id(6), now + 1));
  CHECK(bank.door(7)->snapshot().phase == Phase::WaitingClose);
  CHECK(bank.door(7)->closed(id(3), now + 2));
  auto rerouted = a; rerouted.relay = 8; auto changed = signedIdentity(rerouted);
  CHECK(bank.claim(rerouted, changed, id(7), now + 2) == ClaimResult::Conflict);
  CHECK(bank.claim(a, digestA, id(7), now + 2) == ClaimResult::Duplicate);
  size_t cursor = 0; Event event{}; uint8_t relay = 0;
  for (unsigned i = 0; i < 6; ++i) {
    CHECK(bank.nextEvent(cursor, event, relay));
    CHECK(relay == (i % 2 ? 8 : 7));
    CHECK(static_cast<unsigned>(event.kind) == i / 2);
    CHECK(!bank.acknowledge(relay == 7 ? 8 : 7, event.id.data()));
    CHECK(bank.acknowledge(relay, event.id.data()));
  }
  CHECK(!bank.nextEvent(cursor, event, relay));
  DoorBank reboot;
  CHECK(reboot.add(7, first.saved, [](const Snapshot&) { return true; }));
  CHECK(reboot.add(8, second.saved, [](const Snapshot&) { return true; }));
  CHECK(reboot.claim(rerouted, changed, id(7), now + 2) == ClaimResult::Conflict);
  CHECK(reboot.claim(a, digestA, id(7), now + 1) == ClaimResult::Rejected);
}

void independentRecoveryAndGlobalStorageFailure() {
  Storage first, second; DoorBank bank;
  CHECK(bank.add(7, first.saved, [&](const Snapshot& s) { first.saved = s; return !first.fail; }));
  CHECK(bank.add(8, second.saved, [&](const Snapshot& s) { second.saved = s; return true; }));
  auto a = command(); auto digestA = signedIdentity(a);
  CHECK(bank.claim(a, digestA, id(1), now) == ClaimResult::Accepted);
  CHECK(bank.door(7)->beginPulse(now));
  DoorBank reboot;
  CHECK(reboot.add(7, first.saved, [&](const Snapshot& s) { if (first.fail) return false; first.saved = s; return true; }));
  CHECK(reboot.add(8, second.saved, [&](const Snapshot& s) { second.saved = s; return true; }));
  CHECK(reboot.door(7)->recoveryRequired()); CHECK(!reboot.door(8)->recoveryRequired());
  auto b = command(); b.id = id(20); b.relay = 8; auto digestB = signedIdentity(b);
  CHECK(reboot.claim(b, digestB, id(4), now) == ClaimResult::Accepted);
  CHECK(reboot.door(8)->beginPulse(now)); CHECK(!reboot.door(7)->beginPulse(now));
  first.fail = true; CHECK(!reboot.acknowledge(7, id(1))); CHECK(!reboot.healthy());
  auto c = command(); c.id = id(21); c.relay = 8; auto digestC = signedIdentity(c);
  CHECK(reboot.claim(c, digestC, id(9), now) == ClaimResult::StorageError);
}

int main() {
  const std::vector<std::pair<const char*, void (*)()>> tests = {
    {"shared HMAC vector and field tampering", vectorAndFields}, {"canonical validation", canonicalValidation},
    {"event domain separation", eventDomain}, {"durable claim before pulse and replay", claimBeforePulseAndReplay},
    {"restart at each uncertain phase", crashWindows}, {"storage failures fail closed", storageFailure},
    {"sensor ordering and durable event queue", sensorOrderAndDurableEvents}, {"event capacity reserves full cycle", pendingQueueCapacity},
    {"unexpired claims and clock rollback", claimsCapacityAndClockRollback}, {"corruption and pulse expiry", corruptedStateAndExpiry},
    {"portable snapshot encoding and byte corruption", storageEncoding},
    {"parallel doors, fair events and global replay guard", parallelDoorsAndGlobalReplay},
    {"independent recovery and global storage failure", independentRecoveryAndGlobalStorageFailure}
  };
  for (const auto& test : tests) {
    try { test.second(); std::cout << "PASS " << test.first << '\n'; }
    catch (const std::exception& error) { std::cerr << "FAIL " << test.first << ": " << error.what() << '\n'; return 1; }
  }
  std::cout << tests.size() << " firmware core tests passed\n";
}
