# Action Cable Replay Gate (Q05)

Validates Action Cable WebSocket frame sequence parity between the reference Rails app and the C# port, ensuring every channel frame sequence equals the reference:
1. **Raw WebSocket Clients**: RFC 6455 framing over `actioncable-v1-json` subprotocol.
2. **Channel Parity**: Subscriptions, confirmations, rejections, broadcasts, and Turbo Streams for every Campfire channel.
3. **Revocation & Disconnects**:
   - Room membership revocation: connection disconnects with `reason: "remote", reconnect: true`; resubscription rejected.
   - User deactivation: connection disconnects with `reason: "remote", reconnect: false`; reconnection rejected.
   - User ban: connection disconnects with `reason: "remote", reconnect: false`; reconnection rejected.
4. **Lag Disconnect**: Subscriber lagging behind queue capacity is disconnected with `reason: null, reconnect: true`.

## Channel Coverage

| Channel | Identifier / Parameters | Behaviors Tested |
|---|---|---|
| `HeartbeatChannel` | `{}` | `confirm_subscription` |
| `PresenceChannel` | `{"room_id": <id>}` | Member `confirm_subscription`, `present`/`refresh` actions, non-member `reject_subscription`, non-numeric id `reject_subscription` |
| `ReadRoomsChannel` | `{}` | `confirm_subscription`, read room broadcasts |
| `RoomChannel` | `{"room_id": <id>}` | Member `confirm_subscription`, non-member `reject_subscription`, missing id `reject_subscription` |
| `RoomMessagesChannel` | `{"signed_stream_name": <signed>}` | Valid stream `confirm_subscription`, forged stream `reject_subscription`, missing stream `reject_subscription` |
| `TypingNotificationsChannel` | `{"room_id": <id>}` | Member `confirm_subscription`, `start`/`stop` actions broadcast to peers, non-member `reject_subscription` |
| `UnreadRoomsChannel` | `{}` | `confirm_subscription`, `subscribed` action, unread fanout broadcasts |
| `Turbo::StreamsChannel` | `{"signed_stream_name": <signed>}` | Valid stream `confirm_subscription`, HTML `<turbo-stream>` frame delivery, forged stream `reject_subscription` |
| `ApplicationCable::Channel` | `{}` | `confirm_subscription` (empty base channel) |
| `(connection)` | N/A | Missing / invalid session token disconnected with `reason: "unauthorized", reconnect: false` |
| `(revocation)` | N/A | Membership revocation (`remote`, `reconnect: true`), deactivation (`remote`, `reconnect: false`), ban (`remote`, `reconnect: false`) |
| `(lag)` | N/A | Buffer overflow / queue capacity reached disconnected with `reason: null, reconnect: true` |

## Architecture

- `parity/cable/client.py`: Raw WebSocket client implementing RFC 6455 masking, handshake, control frames (ping/pong/close), and Action Cable framing.
- `parity/cable/normalizer.py`: Normalizes Action Cable frames per `reference-rust/parity/capture/network.ts`: ignores pings, sorts JSON identifiers, and normalizes Turbo Streams HTML.
- `parity/cable/diff.py`: Structural diff engine detecting type, identifier, count, payload, and disconnect reason/reconnect mismatches.
- `parity/cable/defects.py`: Injects 7 planted defects (`wrong-outcome`, `missing-message`, `extra-message`, `modified-payload`, `wrong-disconnect-reason`, `wrong-reconnect-flag`, `missing-lag-disconnect`) to ensure the diff engine catches divergence.
- `parity/cable/scenarios.py`: Channel scenario runner and golden vector validator.
- `parity/cable/Server.cs`: C# candidate launcher with `/cable` mounted, `RevocationGuard`, and `DomainSeams`.
- `parity/cable/run`: Command-line executable CLI.
- `parity/cable/test_cable.py`: 26 offline unit tests.
- `parity/cable/results.json`: Execution verification report.

## Running Tests

### Offline Selftest & Planted Defects
```bash
parity/cable/run --selftest
# or:
python3 -m unittest discover -s parity/cable -p 'test_*.py'
```

### Replay Against Golden Reference Vectors
```bash
parity/cable/run --replay --report parity/cable/results.json
```

### Live Server Comparison
```bash
# 1. Start C# candidate server:
dotnet run parity/cable/Server.cs -- 3200 parity/.seed/default/db/production.sqlite3

# 2. Run replay against reference and candidate:
parity/cable/run ws://127.0.0.1:3100/cable ws://127.0.0.1:3200/cable --report parity/cable/results.json
```
