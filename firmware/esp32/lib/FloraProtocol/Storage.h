#pragma once
#include "Journal.h"
#include <vector>

namespace flora {
std::vector<uint8_t> encodeSnapshot(const Snapshot& snapshot);
bool decodeSnapshot(const uint8_t* bytes, size_t size, Snapshot& snapshot);
}  // namespace flora
