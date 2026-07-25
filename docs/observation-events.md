# Observation event catalog v1

`game-events.ndjson` carries public, normalized events emitted by ArenaGS. An
event code is a versioned public identifier: consumers may group or filter on
the code, but must treat `public_summary` as display text rather than a command
or a source of hidden reasoning. Every event has a stable `event_id`, game date,
bounded entity IDs, and—where an action caused it—a correlation ID.

| Code | Meaning |
| --- | --- |
| `ARENA-FINANCE-UPDATED` | A typed loan action completed. |
| `ARENA-PROJECT-PROPOSED` | A build-route project was accepted and persisted. |
| `ARENA-PROJECT-VALIDATING` | A persisted project entered native validation. |
| `ARENA-PROJECT-SURVEYING` | A bounded station, depot, or path survey began. |
| `ARENA-PROJECT-INFRASTRUCTURE` | Placement survey completed and infrastructure work began. |
| `ARENA-STATION-CREATED` | A road station was created. |
| `ARENA-DEPOT-CREATED` | A road depot was created. |
| `ARENA-ROUTE-PROGRESS` | A bounded path/replan/construction milestone occurred. |
| `ARENA-PROJECT-BUYING-VEHICLES` | Infrastructure completed and vehicle selection began. |
| `ARENA-VEHICLE-CREATED` | A compatible route vehicle was purchased. |
| `ARENA-PROJECT-CONFIGURING-ORDERS` | Required station orders are being configured. |
| `ARENA-PROJECT-VERIFYING` | A route is being checked for real movement. |
| `ARENA-ROUTE-OPERATING` | Stations, depot access, orders, and non-depot movement were verified. |
| `ARENA-ROUTE-FLEET-ADJUSTING` | An expand, reduce, or replace fleet request was persisted. |
| `ARENA-ROUTE-FLEET-PROGRESS` | A vehicle is moving through a bounded fleet-adjustment step. |
| `ARENA-VEHICLE-REMOVED` | A route vehicle was sold during a reduction or replacement. |
| `ARENA-ROUTE-FLEET-UPDATED` | A persisted fleet adjustment completed. |
| `ARENA-ROUTE-FLEET-FAILED` | A fleet adjustment stopped safely. |
| `ARENA-PROJECT-RECOVERING` | A project stopped new work and entered deterministic recovery. |
| `ARENA-PROJECT-FAILED` | Recovery completed; retained partial assets remain inspectable. |

Adding, removing, or changing an event's semantics requires a new observation
contract decision before a published benchmark depends on it.
