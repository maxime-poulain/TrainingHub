#!/usr/bin/env bash
#
# Starts the four infrastructure dependencies a developer runs the hosts against — SQL Server,
# the object store, the mail server and the telemetry dashboard — and nothing else. The hosts
# themselves are meant to run from an IDE or `dotnet run`, whose Development settings already
# point at localhost.
#
# The list of what starts is not written here, and that is the point: the compose file keeps the
# three application services behind the `full` profile (ADR 0075), so a bare `docker compose up`
# is the dependencies alone and this script cannot drift from it. `--wait` returns only once every
# healthcheck passes, so a zero exit code means ready rather than merely started.
#
# The whole stack, containers included, is `docker compose --profile full up -d --build`.
# Stopping either is `docker compose down`.

set -euo pipefail
cd "$(dirname "$0")/.."

docker compose up -d --wait

echo
echo "SQL Server    -> running   localhost:1433 (sa / Password@)"
echo "Image storage -> running   localhost:8333 (S3), UI at http://localhost:9333"
echo "Mailpit       -> running   localhost:1025 (SMTP), UI at http://localhost:8025"
echo "Telemetry     -> running   localhost:4317 (OTLP), UI at http://localhost:18888"
echo
echo "Run the hosts from your IDE or with dotnet run; stop the dependencies with: docker compose down"
