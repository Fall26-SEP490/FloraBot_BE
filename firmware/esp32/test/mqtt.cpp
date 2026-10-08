#include "PubSubClient.h"
#include "ShimClient.h"
#include <iostream>
#include <stdexcept>

#define CHECK(condition) do { if (!(condition)) throw std::runtime_error("Check failed at line " + std::to_string(__LINE__) + ": " #condition); } while (false)

static unsigned received = 0;
static uint16_t acknowledged = 0;
static uint8_t granted = 255;
static uint8_t flags = 0;
static PubSubClient* active = nullptr;
void onMessage(char* topic, uint8_t* payload, unsigned length) {
  CHECK(std::string(topic) == "t"); CHECK(length == 1 && payload[0] == 'x');
  flags = active->incomingFlags(); ++received;
}
void onAck(uint16_t id) { acknowledged = id; }
void onSubscribe(uint16_t id, uint8_t qos) { CHECK(id == 7); granted = qos; }
void connect(PubSubClient& client, ShimClient& socket) {
  uint8_t connack[] = {0x20, 2, 0, 0}; socket.respond(connack, sizeof(connack));
  client.setServer("localhost", 8883); client.setCallback(onMessage);
  CHECK(client.connect("test-device")); active = &client;
}

int main() {
  try {
    {
      ShimClient socket; PubSubClient client(socket); connect(client, socket);
      client.setPubackCallback(onAck); client.setSubackCallback(onSubscribe);
      const auto packetId = client.reservePacketId(); CHECK(packetId != 0);
      uint8_t expected[] = {0x32, 6, 0, 1, 't', static_cast<uint8_t>(packetId >> 8), static_cast<uint8_t>(packetId), 'x'};
      socket.expect(expected, sizeof(expected)); auto before = socket.received();
      CHECK(client.publishQos1("t", reinterpret_cast<const uint8_t*>("x"), 1, packetId));
      CHECK(socket.received() - before == sizeof(expected)); CHECK(!socket.error());
      CHECK(acknowledged == 0);
      uint8_t ack[] = {0x40, 2, expected[5], expected[6]}; socket.respond(ack, sizeof(ack));
      CHECK(client.loop()); CHECK(acknowledged == packetId);
      expected[0] = 0x3a; socket.expect(expected, sizeof(expected));
      CHECK(client.publishQos1("t", reinterpret_cast<const uint8_t*>("x"), 1, packetId, true)); CHECK(!socket.error());
      CHECK(!client.publishQos1("t", nullptr, 1, packetId));
      CHECK(!client.publishQos1("t", reinterpret_cast<const uint8_t*>("x"), 1, 0));
      CHECK(!client.publishQos1("t", reinterpret_cast<const uint8_t*>("x"), 65535, packetId));
      uint8_t suback[] = {0x90, 3, 0, 7, 1}; socket.respond(suback, sizeof(suback));
      CHECK(client.loop()); CHECK(granted == 1);
      std::cout << "PASS QoS 1 bytes, DUP, PUBACK, SUBACK and bounds\n";
    }
    {
      ShimClient socket; PubSubClient client(socket); connect(client, socket);
      uint8_t message[] = {0x33, 6, 0, 1, 't', 0, 9, 'x'};
      uint8_t expectedAck[] = {0x40, 2, 0, 9};
      socket.respond(message, sizeof(message)); socket.expect(expectedAck, sizeof(expectedAck));
      CHECK(client.loop()); CHECK(received == 1); CHECK(flags == 3); CHECK(!socket.error());
      std::cout << "PASS retained and QoS metadata visible before callback\n";
    }
    {
      ShimClient socket; PubSubClient client(socket); connect(client, socket);
      uint8_t malformed[] = {0x32, 3, 0, 9, 't'}; socket.respond(malformed, sizeof(malformed));
      uint8_t disconnect[] = {0xe0, 0}; socket.expect(disconnect, sizeof(disconnect));
      CHECK(!client.loop()); CHECK(received == 1); CHECK(!client.connected());
      std::cout << "PASS malformed PUBLISH cannot reach application\n";
    }
    {
      ShimClient socket; PubSubClient client(socket); connect(client, socket);
      client.setPubackCallback(onAck); acknowledged = 0;
      uint8_t malformed[] = {0x40, 2, 0, 0}; socket.respond(malformed, sizeof(malformed));
      uint8_t disconnect[] = {0xe0, 0}; socket.expect(disconnect, sizeof(disconnect));
      CHECK(!client.loop()); CHECK(acknowledged == 0);
      std::cout << "PASS invalid PUBACK cannot clear pending event\n";
    }
  } catch (const std::exception& error) { std::cerr << error.what() << '\n'; return 1; }
  std::cout << "4 MQTT transport tests passed\n";
}
