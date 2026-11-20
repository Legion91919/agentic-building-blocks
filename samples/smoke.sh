#!/usr/bin/env bash
# Smoke test for samples/Blocks.Sample.Api. Start the API first:
#   ASPNETCORE_ENVIRONMENT=Development ASPNETCORE_URLS=http://localhost:5199 dotnet run --project samples/Blocks.Sample.Api
set -euo pipefail
BASE=${BASE:-http://localhost:5199}
EMAIL="smoke-$(date +%s)@example.com"
PASS="a-long-demo-password"
j() { python3 -c "import sys,json; print(json.load(sys.stdin)$1)"; }
show() { echo; echo "\$ $1"; }

show "GET /health/ready"; curl -s -i "$BASE/health/ready" | grep -E "^HTTP|X-Correlation-Id|^Healthy"
show "POST /auth/register"; curl -s -X POST "$BASE/auth/register" -H 'Content-Type: application/json' -d "{\"email\":\"$EMAIL\",\"password\":\"$PASS\"}" -w '  -> %{http_code}\n'
show "POST /auth/login"; LOGIN=$(curl -s -X POST "$BASE/auth/login" -H 'Content-Type: application/json' -d "{\"email\":\"$EMAIL\",\"password\":\"$PASS\"}")
TOKEN=$(echo "$LOGIN" | j "['accessToken']")
echo "tokenType=$(echo "$LOGIN" | j "['tokenType']") expiresIn=$(echo "$LOGIN" | j "['expiresIn']") accessToken=${TOKEN:0:16}... refreshToken=present"
AUTH=(-H "Authorization: Bearer $TOKEN")
show "GET /auth/me"; curl -s "$BASE/auth/me" "${AUTH[@]}" -w '  -> %{http_code}\n'
show "POST /demo/orders (Idempotency-Key: order-1) #1"; curl -s -i -X POST "$BASE/demo/orders" "${AUTH[@]}" -H 'Content-Type: application/json' -H 'Idempotency-Key: order-1' -d '{"sku":"tea","quantity":3,"unitPrice":2.50,"currency":"EUR"}' | grep -E "^HTTP|^Location|^Idempotency-Replayed|orderId"
show "POST /demo/orders (same key, same body) #2"; curl -s -i -X POST "$BASE/demo/orders" "${AUTH[@]}" -H 'Content-Type: application/json' -H 'Idempotency-Key: order-1' -d '{"sku":"tea","quantity":3,"unitPrice":2.50,"currency":"EUR"}' | grep -E "^HTTP|^Location|^Idempotency-Replayed|orderId"
show "POST /demo/orders (same key, different body)"; curl -s -X POST "$BASE/demo/orders" "${AUTH[@]}" -H 'Content-Type: application/json' -H 'Idempotency-Key: order-1' -d '{"sku":"tea","quantity":4,"unitPrice":2.50,"currency":"EUR"}' -w '  -> %{http_code}\n'
show "POST /demo/orders (no key)"; curl -s -X POST "$BASE/demo/orders" "${AUTH[@]}" -H 'Content-Type: application/json' -d '{"sku":"tea","quantity":1,"unitPrice":2.50,"currency":"EUR"}' -w '  -> %{http_code}\n'
show "GET /media/hd (no subscription)"; curl -s "$BASE/media/hd" "${AUTH[@]}" -w '  -> %{http_code}\n'
show "POST /subscriptions/checkout {plus}"; CHECKOUT=$(curl -s -X POST "$BASE/subscriptions/checkout" "${AUTH[@]}" -H 'Content-Type: application/json' -d '{"planId":"plus"}'); echo "$CHECKOUT"
SESSION=$(echo "$CHECKOUT" | j "['sessionId']")
show "POST /subscriptions/dev/checkout/{session}/complete (signed fake webhook)"; curl -s -X POST "$BASE/subscriptions/dev/checkout/$SESSION/complete" "${AUTH[@]}" -w '  -> %{http_code}\n'
show "GET /media/hd (subscribed)"; curl -s "$BASE/media/hd" "${AUTH[@]}" -w '  -> %{http_code}\n'
show "POST /demo/notify-me"; curl -s -X POST "$BASE/demo/notify-me" "${AUTH[@]}" -w '  -> %{http_code}\n'
sleep 2
show "GET /notifications/inbox/unread-count"; curl -s "$BASE/notifications/inbox/unread-count" "${AUTH[@]}" -w '  -> %{http_code}\n'
show "GET /notifications/inbox (titles)"; curl -s "$BASE/notifications/inbox" "${AUTH[@]}" | j "['items']" | python3 -c "import sys,ast; [print(' -', i['category'], '|', i['title']) for i in ast.literal_eval(sys.stdin.read())]"
show "POST /auth/logout then GET /auth/me"; curl -s -X POST "$BASE/auth/logout" "${AUTH[@]}" -w '  logout -> %{http_code}\n'; curl -s -o /dev/null "$BASE/auth/me" "${AUTH[@]}" -w '  me -> %{http_code}\n'
