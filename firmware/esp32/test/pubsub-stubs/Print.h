#pragma once
#include <cstddef>
#include <cstdint>
// Minimal local substitute for the Arduino Print interface in host-only tests.
class Print {
 public:
  virtual ~Print() = default;
  virtual size_t write(uint8_t) = 0;
};
