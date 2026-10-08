"""FloraBot v1 wire format, shared by the MQTT simulator and protocol tests."""

import hashlib
import hmac
import json
import re
import sqlite3
import uuid


def canonical_command(command):
    if command.get("version") != 1 or type(command.get("version")) is not int:
        raise ValueError("Unsupported version")
    hardware = command["hardware_id"]
    if not isinstance(hardware, str) or not re.fullmatch(r"[A-Za-z0-9_-]{1,64}", hardware):
        raise ValueError("Invalid hardware")
    identifiers = [str(uuid.UUID(command[name])) for name in ("cmd_id", "slot_id")]
    if any(value == str(uuid.UUID(int=0)) for value in identifiers):
        raise ValueError("Empty identifier")
    relay = command["relay_channel"]
    issued, expires = command["issued_at"], command["expires_at"]
    if type(relay) is not int or not 0 <= relay <= 63:
        raise ValueError("Invalid relay")
    if type(issued) is not int or type(expires) is not int or not 0 < issued < expires:
        raise ValueError("Invalid time window")
    if command["purpose"] not in ("CUSTOMER_PICKUP", "SELLER_ACCESS"):
        raise ValueError("Invalid purpose")
    return "\n".join([
        "florabot.unlock.v1", hardware, *identifiers, str(relay), command["purpose"],
        str(issued), str(expires),
    ]).encode("ascii")


def verify_command(command, hardware, key, now):
    try:
        canonical = canonical_command(command)
        signature = command["signature"]
        return (
            len(key) == 32 and command["hardware_id"] == hardware
            and command["issued_at"] <= now < command["expires_at"]
            and isinstance(signature, str) and len(signature) == 64
            and hmac.compare_digest(bytes.fromhex(signature), hmac.digest(key, canonical, "sha256"))
        )
    except (KeyError, TypeError, ValueError, AttributeError):
        return False


def signed_event(hardware, command_id, event, key, now):
    event_id = str(uuid.uuid4())
    command_id = str(uuid.UUID(command_id))
    canonical = "\n".join([
        "florabot.event.v1", hardware, event_id, command_id, event, str(now),
    ]).encode("ascii")
    return dict(version=1, hardware_id=hardware, event_id=event_id, cmd_id=command_id,
                event=event, occurred_at=now, signature=hmac.digest(key, canonical, "sha256").hex())


class CommandStore:
    """Claim before actuating: a crash cannot cause a repeated relay pulse."""

    def __init__(self, path):
        self.db = sqlite3.connect(path)
        self.db.execute("PRAGMA synchronous=FULL")
        self.db.execute("CREATE TABLE IF NOT EXISTS commands (id TEXT PRIMARY KEY, digest TEXT NOT NULL)")
        self.db.execute("CREATE TABLE IF NOT EXISTS events (sequence INTEGER PRIMARY KEY, payload TEXT NOT NULL, delivered INTEGER NOT NULL DEFAULT 0)")
        self.db.commit()

    def process(self, command, hardware, key, now, pulse):
        if not verify_command(command, hardware, key, now):
            return "rejected"
        command_id = str(uuid.UUID(command["cmd_id"]))
        digest = hashlib.sha256(canonical_command(command)).hexdigest()
        with self.db:
            existing = self.db.execute("SELECT digest FROM commands WHERE id=?", (command_id,)).fetchone()
            if existing:
                return "duplicate" if existing[0] == digest else "rejected"
            self.db.execute("INSERT INTO commands VALUES (?,?)", (command_id, digest))
        # A real device must persist this claim in NVS before energizing the relay.
        # This callback simulates a full sensor-confirmed opening and closing cycle.
        pulse(command["relay_channel"])
        with self.db:
            for event in ("ACK", "OPENED", "CLOSED"):
                payload = signed_event(hardware, command_id, event, key, now)
                self.db.execute("INSERT INTO events(payload) VALUES (?)", (json.dumps(payload),))
        return "executed"

    def pending(self):
        return self.db.execute("SELECT sequence,payload FROM events WHERE delivered=0 ORDER BY sequence").fetchall()

    def delivered(self, sequence):
        with self.db:
            self.db.execute("UPDATE events SET delivered=1 WHERE sequence=?", (sequence,))

    def close(self):
        self.db.close()
