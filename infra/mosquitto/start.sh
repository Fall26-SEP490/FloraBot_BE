#!/bin/sh
set -eu
cp /provisioned/ca.crt /provisioned/server.crt /provisioned/server.key /provisioned/passwords /mosquitto/secure/
cp /mosquitto/config/acl /mosquitto/secure/acl
chown -R mosquitto:mosquitto /mosquitto/secure
chmod 700 /mosquitto/secure
chmod 600 /mosquitto/secure/*
exec /docker-entrypoint.sh mosquitto -c /mosquitto/config/mosquitto.conf
