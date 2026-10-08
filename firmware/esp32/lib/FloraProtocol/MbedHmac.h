#pragma once
#include "Protocol.h"
#include <mbedtls/md.h>

namespace flora {
inline bool mbedHmac(const Digest& key, const std::string& message, Digest& output) {
  const auto* info = mbedtls_md_info_from_type(MBEDTLS_MD_SHA256);
  return info && mbedtls_md_hmac(info, key.data(), key.size(),
      reinterpret_cast<const uint8_t*>(message.data()), message.size(), output.data()) == 0;
}
}  // namespace flora
