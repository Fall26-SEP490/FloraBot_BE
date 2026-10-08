FROM debian:bookworm-slim AS native-test
RUN apt-get update && apt-get install -y --no-install-recommends g++ libssl-dev ca-certificates \
    && rm -rf /var/lib/apt/lists/*
WORKDIR /firmware
COPY lib/FloraProtocol lib/FloraProtocol
COPY test/native.cpp test/native.cpp
RUN g++ -std=c++17 -Wall -Wextra -Werror -fsanitize=address,undefined -fno-pie -no-pie -g \
    -Ilib/FloraProtocol test/native.cpp lib/FloraProtocol/*.cpp -lcrypto -o /tmp/firmware-tests \
    && timeout 60 /tmp/firmware-tests
COPY lib/PubSubClient lib/PubSubClient
COPY test/pubsub-stubs test/pubsub-stubs
COPY test/mqtt.cpp test/mqtt.cpp
RUN g++ -std=c++17 -Wall -fsanitize=address,undefined -fno-pie -no-pie -g \
    -Itest/pubsub-stubs -Ilib/PubSubClient/src test/mqtt.cpp test/pubsub-stubs/*.cpp \
    lib/PubSubClient/src/PubSubClient.cpp -o /tmp/mqtt-tests && timeout 60 /tmp/mqtt-tests
RUN apt-get update && apt-get install -y --no-install-recommends libmbedtls-dev \
    && rm -rf /var/lib/apt/lists/* \
    && g++ -std=c++17 -Wall -Wextra -Werror -fsanitize=address,undefined -fno-pie -no-pie -g \
    -DFLORA_MBEDTLS -Ilib/FloraProtocol test/native.cpp lib/FloraProtocol/*.cpp -lmbedcrypto \
    -o /tmp/mbedtls-tests && timeout 60 /tmp/mbedtls-tests

FROM python:3.12-slim AS firmware-tools
RUN apt-get update && apt-get install -y --no-install-recommends git ca-certificates \
    && rm -rf /var/lib/apt/lists/* && pip install --no-cache-dir platformio==6.2.0
WORKDIR /firmware
COPY platformio.ini partitions.csv ./
RUN pio pkg install -e esp32dev

FROM firmware-tools AS firmware-build
COPY include include
COPY lib lib
COPY src src
COPY test/config.compilecheck.h test/config.compilecheck.h
COPY test/verify_partitions.py test/verify_partitions.py
RUN pio run -e esp32dev -e esp32dev-compilecheck && python test/verify_partitions.py

FROM scratch AS firmware-artifacts
COPY --from=firmware-build /firmware/.pio/build/esp32dev/firmware.bin /firmware.bin
COPY --from=firmware-build /firmware/.pio/build/esp32dev/firmware.factory.bin /firmware.factory.bin
COPY --from=firmware-build /firmware/.pio/build/esp32dev/bootloader.bin /bootloader.bin
COPY --from=firmware-build /firmware/.pio/build/esp32dev/partitions.bin /partitions.bin
