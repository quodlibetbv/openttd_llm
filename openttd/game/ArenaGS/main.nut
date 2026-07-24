/*
 * ArenaGS is the authoritative Phase 03 GameScript boundary. AdminPort is
 * authenticated by OpenTTD before an event reaches this code; this dispatcher
 * then applies the Arena envelope allowlist, run binding, bounds, chunk checks,
 * and persisted idempotency ledger before any game-side operation.
 */
class ArenaGS extends GSController {
    static PROTOCOL_VERSION = "1.0";
    static MAX_IDENTIFIER_LENGTH = 128;
    static MAX_LEDGER_ENTRIES = 64;
    static MAX_TRANSFERS = 8;
    static MAX_CHUNKS = 48;
    static MAX_CHUNK_DATA = 512;
    static MAX_LOGICAL_BYTES = 12288;
    /* Four seconds at OpenTTD's 37 ticks/second leaves a bounded retry window
     * inside the bridge-smoke request timeout. */
    static TRANSFER_TIMEOUT_TICKS = 148;

    _active_run_id = null;
    _ledger = null;
    _ledger_order = null;
    _transfers = null;
    _tick = 0;
    _message_sequence = 0;
    _finalized = false;

    function Save() {
        return {
            protocol_version = ArenaGS.PROTOCOL_VERSION,
            active_run_id = this._active_run_id,
            ledger = this._ledger,
            ledger_order = this._ledger_order,
            finalized = this._finalized,
            message_sequence = this._message_sequence,
        };
    }

    function Load(version, data) {
        this._ledger = {};
        this._ledger_order = [];
        this._transfers = {};
        if (data != null && typeof data == "table" && data.rawin("protocol_version") && data.protocol_version == ArenaGS.PROTOCOL_VERSION) {
            if (data.rawin("active_run_id")) this._active_run_id = data.active_run_id;
            if (data.rawin("ledger") && typeof data.ledger == "table") this._ledger = data.ledger;
            if (data.rawin("ledger_order") && typeof data.ledger_order == "array") this._ledger_order = data.ledger_order;
            if (data.rawin("finalized") && typeof data.finalized == "bool") this._finalized = data.finalized;
            if (data.rawin("message_sequence") && typeof data.message_sequence == "integer") this._message_sequence = data.message_sequence;
        }

        /* Loading a fixed starting save must be observable while it remains paused. */
        GSLog.Info("ARENA_PHASE02_GAMESCRIPT_READY");
    }

    function Start() {
        if (this._ledger == null) this._ledger = {};
        if (this._ledger_order == null) this._ledger_order = [];
        if (this._transfers == null) this._transfers = {};

        /* Unlike Load(), this signal proves that the simulation has advanced
         * into the GameScript's cancellable event loop. */
        GSLog.Info("ARENA_PHASE03_GAMESCRIPT_ACTIVE");
        while (true) {
            this._tick += 1;
            this.ProcessEvents();
            this.ExpireTransfers();
            if (this._tick % 74 == 0) {
                /* Retained Phase 02 readiness signal for lifecycle supervision. */
                GSLog.Info("ARENA_PHASE02_GAMESCRIPT_READY");
            }

            this.Sleep(1);
        }
    }

    function ProcessEvents() {
        while (GSEventController.IsEventWaiting()) {
            local event = GSEventController.GetNextEvent();
            if (event == null || event.GetEventType() != GSEvent.ET_ADMIN_PORT) continue;

            local envelope = GSEventAdminPort.Convert(event).GetObject();
            if (envelope == null || typeof envelope != "table") {
                GSLog.Error("ARENA_PROTOCOL_INVALID_MESSAGE");
                continue;
            }

            this.ProcessEnvelope(envelope, false);
        }
    }

    function ProcessEnvelope(envelope, from_chunk) {
        local validation = this.ValidateEnvelope(envelope);
        if (validation != null) {
            this.SendError(envelope, validation, "ArenaGS rejected an invalid protocol envelope.");
            return;
        }

        if (envelope.message_type == "chunk") {
            this.AcceptChunk(envelope);
            return;
        }

        if (this._active_run_id == null) {
            if (envelope.message_type != "hello") {
                this.SendError(envelope, "ARENA-PROTOCOL-STALE-CORRELATION", "ArenaGS has not accepted a hello for this run.");
                return;
            }

            this._active_run_id = envelope.run_id;
        }

        if (envelope.run_id != this._active_run_id) {
            this.SendError(envelope, "ARENA-PROTOCOL-STALE-CORRELATION", "The protocol request belongs to a different run.");
            return;
        }

        if (this._finalized && envelope.message_type != "heartbeat") {
            this.SendError(envelope, "ARENA-PROTOCOL-INVALID-MESSAGE", "The GameScript has already finalized this bridge session.");
            return;
        }

        if (!this.RequiresIdempotencyKey(envelope.message_type)) {
            this.SendError(envelope, "ARENA-PROTOCOL-INVALID-MESSAGE", "ArenaGS accepts only allowlisted request messages from AdminPort.");
            return;
        }

        if (this.ReplayLedgerResult(envelope)) return;

        switch (envelope.message_type) {
            case "hello":
                this.RecordAndSend(envelope, "capabilities", {
                    protocol_version = ArenaGS.PROTOCOL_VERSION,
                    max_direct_message_bytes = 8192,
                    max_logical_payload_bytes = ArenaGS.MAX_LOGICAL_BYTES,
                    chunk_encoding = "base64_utf8",
                    capabilities = [
                        "heartbeat",
                        "pause",
                        "resume",
                        "snapshot",
                        "idempotency",
                        "chunking",
                    ],
                });
                this.SendEnvelope("heartbeat", envelope, this.HeartbeatPayload());
                break;

            case "heartbeat":
                this.RecordAndSend(envelope, "heartbeat", this.HeartbeatPayload());
                break;

            case "pause_request":
                local pause_changed = GSGame.Pause();
                this.RecordAndSend(envelope, "pause_result", {
                    changed = pause_changed,
                    paused = GSGame.IsPaused(),
                });
                break;

            case "resume_request":
                local resume_changed = GSGame.Unpause();
                this.RecordAndSend(envelope, "resume_result", {
                    changed = resume_changed,
                    paused = GSGame.IsPaused(),
                });
                break;

            case "snapshot_request":
                if (!from_chunk && envelope.payload.rawin("chunk_probe_bytes") && this.IsChunkProbeRequest(envelope.payload.chunk_probe_bytes)) {
                    this.SendChunkedSnapshotProbe(envelope, envelope.payload.chunk_probe_bytes);
                } else {
                    local snapshot = this.SnapshotPayload();
                    if (from_chunk) {
                        snapshot.chunked_payload_bytes <- envelope.payload.chunked_payload_bytes;
                        snapshot.chunk_checksum <- envelope.payload.chunk_checksum;
                    }

                    this.RecordAndSend(envelope, "snapshot_result", snapshot);
                }
                break;

            case "action_request":
                this.RecordAndSend(envelope, "action_result", {
                    status = "rejected",
                    error_code = "ARENA-ACTION-CONSTRAINT-VIOLATION",
                    message = "Action execution is not available until the typed executor phases.",
                });
                break;

            case "camera_request":
                this.RecordAndSend(envelope, "camera_result", {
                    status = "deferred",
                    error_code = "ARENA-ACTION-CONSTRAINT-VIOLATION",
                    message = "Camera direction is not available until Phase 09.",
                });
                break;

            case "checkpoint_request":
                this.RecordAndSend(envelope, "checkpoint_result", {
                    status = "deferred",
                    error_code = "ARENA-ACTION-CONSTRAINT-VIOLATION",
                    message = "Checkpoint persistence remains owned by the run supervisor in this phase.",
                });
                break;

            case "finalize_request":
                this._finalized = true;
                this.RecordAndSend(envelope, "finalize_result", {
                    status = "ready",
                    paused = GSGame.IsPaused(),
                });
                break;

            case "error":
                /* Error is an outbound diagnostic message type; do not execute it as a request. */
                this.SendError(envelope, "ARENA-PROTOCOL-INVALID-MESSAGE", "ArenaGS does not accept inbound error messages.");
                break;

            default:
                this.SendError(envelope, "ARENA-PROTOCOL-INVALID-MESSAGE", "The protocol message type is not a valid ArenaGS request.");
                break;
        }
    }

    function ValidateEnvelope(envelope) {
        local allowed = {
            protocol_version = true,
            message_type = true,
            run_id = true,
            message_id = true,
            correlation_id = true,
            idempotency_key = true,
            payload = true,
        };
        foreach (key, value in envelope) {
            if (!allowed.rawin(key)) return "ARENA-PROTOCOL-INVALID-MESSAGE";
        }

        local required = ["protocol_version", "message_type", "run_id", "message_id", "correlation_id", "payload"];
        foreach (key in required) {
            if (!envelope.rawin(key)) return "ARENA-PROTOCOL-INVALID-MESSAGE";
        }

        if (typeof envelope.protocol_version != "string" || envelope.protocol_version != ArenaGS.PROTOCOL_VERSION) return "ARENA-PROTOCOL-VERSION-MISMATCH";
        if (typeof envelope.message_type != "string" || !this.IsKnownMessageType(envelope.message_type)) return "ARENA-PROTOCOL-INVALID-MESSAGE";
        if (!this.IsIdentifier(envelope.run_id) || !this.IsIdentifier(envelope.message_id) || !this.IsIdentifier(envelope.correlation_id)) return "ARENA-PROTOCOL-INVALID-MESSAGE";
        if (envelope.rawin("idempotency_key") && !this.IsIdentifier(envelope.idempotency_key)) return "ARENA-PROTOCOL-INVALID-MESSAGE";
        if (typeof envelope.payload != "table") return "ARENA-PROTOCOL-INVALID-MESSAGE";
        if (!this.IsBoundedValue(envelope.payload, 0)) return "ARENA-PROTOCOL-MESSAGE-TOO-LARGE";
        if (this.RequiresIdempotencyKey(envelope.message_type) && (!envelope.rawin("idempotency_key") || !this.IsIdentifier(envelope.idempotency_key))) return "ARENA-PROTOCOL-INVALID-MESSAGE";
        return null;
    }

    function IsKnownMessageType(message_type) {
        local known = {
            hello = true, capabilities = true, heartbeat = true,
            pause_request = true, pause_result = true,
            resume_request = true, resume_result = true,
            snapshot_request = true, snapshot_result = true,
            action_request = true, action_progress = true, action_result = true,
            camera_request = true, camera_result = true,
            checkpoint_request = true, checkpoint_result = true,
            finalize_request = true, finalize_result = true,
            error = true, chunk = true,
        };
        return known.rawin(message_type);
    }

    function RequiresIdempotencyKey(message_type) {
        local requests = {
            hello = true, heartbeat = true, pause_request = true, resume_request = true,
            snapshot_request = true, action_request = true, camera_request = true,
            checkpoint_request = true, finalize_request = true, chunk = true,
        };
        return requests.rawin(message_type);
    }

    function IsIdentifier(value) {
        if (typeof value != "string" || value.len() < 1 || value.len() > ArenaGS.MAX_IDENTIFIER_LENGTH) return false;
        for (local index = 0; index < value.len(); index++) {
            local character = value[index];
            local alpha_numeric = (character >= 48 && character <= 57) || (character >= 65 && character <= 90) || (character >= 97 && character <= 122);
            if (!alpha_numeric && character != 46 && character != 45 && character != 95) return false;
        }

        return true;
    }

    function IsBoundedValue(value, depth) {
        if (depth > 16) return false;
        local kind = typeof value;
        if (kind == "string") return value.len() <= ArenaGS.MAX_LOGICAL_BYTES;
        if (kind == "integer" || kind == "bool" || kind == "null") return true;
        if (kind == "array") {
            if (value.len() > 128) return false;
            foreach (entry in value) if (!this.IsBoundedValue(entry, depth + 1)) return false;
            return true;
        }

        if (kind == "table") {
            local count = 0;
            foreach (key, entry in value) {
                count += 1;
                if (count > 64 || typeof key != "string" || key.len() > 128 || !this.IsBoundedValue(entry, depth + 1)) return false;
            }

            return true;
        }

        return false;
    }

    function ReplayLedgerResult(envelope) {
        local key = envelope.idempotency_key;
        if (!this._ledger.rawin(key)) return false;

        local entry = this._ledger[key];
        if (entry.run_id != envelope.run_id || entry.message_type != envelope.message_type || entry.correlation_id != envelope.correlation_id) {
            this.SendError(envelope, "ARENA-PROTOCOL-STALE-CORRELATION", "The idempotency key belongs to a different request.");
            return true;
        }

        this.SendAdminResponse(entry.response);
        return true;
    }

    function RecordAndSend(request, message_type, payload) {
        local response = this.CreateEnvelope(message_type, request, payload);
        this.RecordLedger(request, response);
        this.SendAdminResponse(response);
    }

    function SendEnvelope(message_type, request, payload) {
        this.SendAdminResponse(this.CreateEnvelope(message_type, request, payload));
    }

    function SendError(request, error_code, message) {
        if (request == null || typeof request != "table" || !request.rawin("run_id") || !request.rawin("correlation_id") || !request.rawin("message_id")) {
            GSLog.Error(error_code);
            return;
        }

        if (!this.IsIdentifier(request.run_id) || !this.IsIdentifier(request.correlation_id) || !this.IsIdentifier(request.message_id)) {
            GSLog.Error(error_code);
            return;
        }

        local response = this.CreateEnvelope("error", request, {
            error_code = error_code,
            message = message,
        });
        if (request.rawin("idempotency_key") && this.IsIdentifier(request.idempotency_key) && this.RequiresIdempotencyKey(request.rawin("message_type") ? request.message_type : "")) {
            this.RecordLedger(request, response);
        }

        this.SendAdminResponse(response);
    }

    function SendAdminResponse(response) {
        if (!GSAdmin.Send(response)) {
            GSLog.Error("ARENA_PHASE03_ADMINPORT_RESPONSE_FAILED");
        }
    }

    function CreateEnvelope(message_type, request, payload) {
        this._message_sequence += 1;
        local response = {
            protocol_version = ArenaGS.PROTOCOL_VERSION,
            message_type = message_type,
            run_id = request.run_id,
            message_id = "arena-" + this._message_sequence,
            correlation_id = request.correlation_id,
            payload = payload,
        };
        if (request.rawin("idempotency_key") && this.IsIdentifier(request.idempotency_key)) response.idempotency_key <- request.idempotency_key;
        return response;
    }

    function RecordLedger(request, response) {
        if (!request.rawin("idempotency_key") || !this.IsIdentifier(request.idempotency_key)) return;
        local key = request.idempotency_key;
        this._ledger[key] <- {
            run_id = request.run_id,
            message_type = request.message_type,
            correlation_id = request.correlation_id,
            response = response,
        };
        this._ledger_order.append(key);
        while (this._ledger_order.len() > ArenaGS.MAX_LEDGER_ENTRIES) {
            local expired = this._ledger_order.remove(0);
            if (this._ledger.rawin(expired)) this._ledger.rawdelete(expired);
        }
    }

    function HeartbeatPayload() {
        return {
            ready = true,
            game_date = GSDate.GetCurrentDate(),
            paused = GSGame.IsPaused(),
        };
    }

    function SnapshotPayload() {
        return {
            game_date = GSDate.GetCurrentDate(),
            paused = GSGame.IsPaused(),
            multiplayer = GSGame.IsMultiplayer(),
            landscape = GSGame.GetLandscape(),
        };
    }

    function AcceptChunk(envelope) {
        local parsed = this.ParseChunk(envelope);
        if (parsed == null) {
            this.SendError(envelope, "ARENA-PROTOCOL-CHUNK-INVALID", "The protocol chunk is invalid.");
            return;
        }

        local transfer = this._transfers.rawin(parsed.transfer_id) ? this._transfers[parsed.transfer_id] : null;
        if (transfer == null) {
            if (this.CountTransfers() >= ArenaGS.MAX_TRANSFERS) {
                this.SendError(envelope, "ARENA-PROTOCOL-CHUNK-INVALID", "Too many incomplete chunk transfers are active.");
                return;
            }

            transfer = {
                run_id = envelope.run_id,
                correlation_id = parsed.logical_correlation_id,
                message_type = parsed.logical_message_type,
                message_id = parsed.logical_message_id,
                idempotency_key = parsed.logical_idempotency_key,
                total_chunks = parsed.total_chunks,
                logical_bytes = parsed.logical_bytes,
                checksum = parsed.checksum,
                parts = {},
                last_tick = this._tick,
            };
            this._transfers[parsed.transfer_id] <- transfer;
        } else if (!this.ChunkMatches(transfer, envelope, parsed)) {
            this._transfers.rawdelete(parsed.transfer_id);
            this.SendError(envelope, "ARENA-PROTOCOL-CHUNK-INVALID", "Chunk transfer metadata changed before completion.");
            return;
        }

        if (transfer.parts.rawin(parsed.sequence) && transfer.parts[parsed.sequence] != parsed.data) {
            this._transfers.rawdelete(parsed.transfer_id);
            this.SendError(envelope, "ARENA-PROTOCOL-CHUNK-INVALID", "A duplicate chunk sequence carried different data.");
            return;
        }

        transfer.parts[parsed.sequence] <- parsed.data;
        transfer.last_tick = this._tick;
        if (this.CountParts(transfer.parts) != transfer.total_chunks) return;

        this._transfers.rawdelete(parsed.transfer_id);
        local data = "";
        for (local sequence = 0; sequence < transfer.total_chunks; sequence++) {
            if (!transfer.parts.rawin(sequence)) {
                this.SendError(envelope, "ARENA-PROTOCOL-CHUNK-INVALID", "The completed chunk transfer had a missing sequence.");
                return;
            }

            data += transfer.parts[sequence];
        }

        if (this.Adler32(data) != transfer.checksum || data.len() != this.Base64Length(transfer.logical_bytes)) {
            this.SendError(envelope, "ARENA-PROTOCOL-CHUNK-INVALID", "The completed chunk transfer failed integrity verification.");
            return;
        }

        local logical = {
            protocol_version = ArenaGS.PROTOCOL_VERSION,
            message_type = transfer.message_type,
            run_id = transfer.run_id,
            message_id = transfer.message_id,
            correlation_id = transfer.correlation_id,
            idempotency_key = transfer.idempotency_key,
            payload = {
                chunked_payload_bytes = transfer.logical_bytes,
                chunk_checksum = transfer.checksum,
            },
        };
        this.ProcessEnvelope(logical, true);
    }

    function ParseChunk(envelope) {
        local payload = envelope.payload;
        local fields = [
            "transfer_id", "logical_message_type", "logical_message_id", "logical_correlation_id",
            "logical_idempotency_key", "sequence", "total_chunks", "logical_bytes", "encoding", "checksum", "data",
        ];
        foreach (field in fields) if (!payload.rawin(field)) return null;
        foreach (field, value in payload) {
            local known = false;
            foreach (required in fields) if (field == required) known = true;
            if (!known) return null;
        }

        if (!this.IsIdentifier(payload.transfer_id) || !this.IsIdentifier(payload.logical_message_id) || !this.IsIdentifier(payload.logical_correlation_id) || !this.IsIdentifier(payload.logical_idempotency_key)) return null;
        if (typeof payload.logical_message_type != "string" || !this.RequiresIdempotencyKey(payload.logical_message_type) || payload.logical_message_type == "chunk") return null;
        if (typeof payload.sequence != "integer" || typeof payload.total_chunks != "integer" || typeof payload.logical_bytes != "integer") return null;
        if (payload.sequence < 0 || payload.total_chunks < 1 || payload.total_chunks > ArenaGS.MAX_CHUNKS || payload.sequence >= payload.total_chunks || payload.logical_bytes < 1 || payload.logical_bytes > ArenaGS.MAX_LOGICAL_BYTES) return null;
        if (envelope.correlation_id != payload.logical_correlation_id || !envelope.rawin("idempotency_key") || envelope.idempotency_key != payload.logical_idempotency_key) return null;
        if (typeof payload.encoding != "string" || payload.encoding != "base64_utf8" || typeof payload.checksum != "string" || !this.IsChecksum(payload.checksum) || typeof payload.data != "string" || payload.data.len() < 1 || payload.data.len() > ArenaGS.MAX_CHUNK_DATA || !this.IsBase64(payload.data)) return null;
        return payload;
    }

    function ChunkMatches(transfer, envelope, parsed) {
        return transfer.run_id == envelope.run_id && transfer.correlation_id == parsed.logical_correlation_id && transfer.message_type == parsed.logical_message_type && transfer.message_id == parsed.logical_message_id && transfer.idempotency_key == parsed.logical_idempotency_key && transfer.total_chunks == parsed.total_chunks && transfer.logical_bytes == parsed.logical_bytes && transfer.checksum == parsed.checksum;
    }

    function CountTransfers() {
        local count = 0;
        foreach (key, value in this._transfers) count += 1;
        return count;
    }

    function CountParts(parts) {
        local count = 0;
        foreach (key, value in parts) count += 1;
        return count;
    }

    function ExpireTransfers() {
        local expired = [];
        foreach (transfer_id, transfer in this._transfers) {
            if (this._tick - transfer.last_tick > ArenaGS.TRANSFER_TIMEOUT_TICKS) expired.append(transfer_id);
        }

        foreach (transfer_id in expired) {
            local transfer = this._transfers[transfer_id];
            this._transfers.rawdelete(transfer_id);
            this.SendError({
                run_id = transfer.run_id,
                message_id = transfer.message_id,
                correlation_id = transfer.correlation_id,
                idempotency_key = transfer.idempotency_key,
                message_type = transfer.message_type,
            }, "ARENA-PROTOCOL-CHUNK-TIMEOUT", "A chunk transfer did not complete before the bounded timeout.");
        }
    }

    function IsAscii(value) {
        for (local index = 0; index < value.len(); index++) if (value[index] > 127) return false;
        return true;
    }

    function IsBase64(value) {
        local padding = false;
        local padding_count = 0;
        for (local index = 0; index < value.len(); index++) {
            local character = value[index];
            local alpha_numeric = (character >= 48 && character <= 57) || (character >= 65 && character <= 90) || (character >= 97 && character <= 122);
            if (character == 61) {
                padding = true;
                padding_count += 1;
                if (padding_count > 2) return false;
                continue;
            }

            if (padding || (!alpha_numeric && character != 43 && character != 47)) return false;
        }

        return value.len() > padding_count;
    }

    function IsChecksum(value) {
        if (value.len() != 8) return false;
        for (local index = 0; index < value.len(); index++) {
            local character = value[index];
            if (!((character >= 48 && character <= 57) || (character >= 97 && character <= 102))) return false;
        }

        return true;
    }

    function Adler32(value) {
        local a = 1;
        local b = 0;
        for (local index = 0; index < value.len(); index++) {
            a = (a + value[index]) % 65521;
            b = (b + a) % 65521;
        }

        return this.Hex4(b) + this.Hex4(a);
    }

    function Hex4(value) {
        local characters = "0123456789abcdef";
        local divisors = [4096, 256, 16, 1];
        local result = "";
        foreach (divisor in divisors) {
            local digit = ((value / divisor).tointeger()) % 16;
            result += characters.slice(digit, digit + 1);
        }

        return result;
    }

    function Base64Length(bytes) {
        return (((bytes + 2) / 3).tointeger()) * 4;
    }

    function IsChunkProbeRequest(value) {
        return typeof value == "integer" && value >= 10240 && value <= ArenaGS.MAX_LOGICAL_BYTES;
    }

    function SendChunkedSnapshotProbe(request, bytes) {
        local payload_json = "{\"chunk_probe\":\"" + this.RepeatCharacter("p", bytes) + "\"}";
        local encoded = this.Base64Encode(payload_json);
        local total_chunks = ((encoded.len() + ArenaGS.MAX_CHUNK_DATA - 1) / ArenaGS.MAX_CHUNK_DATA).tointeger();
        if (total_chunks > ArenaGS.MAX_CHUNKS) {
            this.SendError(request, "ARENA-PROTOCOL-MESSAGE-TOO-LARGE", "The requested chunk probe exceeds the protocol chunk bound.");
            return;
        }

        this._message_sequence += 1;
        local logical_message_id = "arena-" + this._message_sequence;
        local transfer_id = "out-" + this._message_sequence + "-" + request.correlation_id;
        local checksum = this.Adler32(encoded);
        for (local sequence = 0; sequence < total_chunks; sequence++) {
            local start = sequence * ArenaGS.MAX_CHUNK_DATA;
            local finish = start + ArenaGS.MAX_CHUNK_DATA;
            if (finish > encoded.len()) finish = encoded.len();
            this.SendAdminResponse({
                protocol_version = ArenaGS.PROTOCOL_VERSION,
                message_type = "chunk",
                run_id = request.run_id,
                message_id = "chunk-out-" + this._message_sequence + "-" + sequence,
                correlation_id = request.correlation_id,
                idempotency_key = request.idempotency_key,
                payload = {
                    transfer_id = transfer_id,
                    logical_message_type = "snapshot_result",
                    logical_message_id = logical_message_id,
                    logical_correlation_id = request.correlation_id,
                    logical_idempotency_key = request.idempotency_key,
                    sequence = sequence,
                    total_chunks = total_chunks,
                    logical_bytes = payload_json.len(),
                    encoding = "base64_utf8",
                    checksum = checksum,
                    data = encoded.slice(start, finish),
                },
            });
        }

        this.RecordLedger(request, {
            protocol_version = ArenaGS.PROTOCOL_VERSION,
            message_type = "snapshot_result",
            run_id = request.run_id,
            message_id = logical_message_id,
            correlation_id = request.correlation_id,
            idempotency_key = request.idempotency_key,
            payload = { chunk_probe_bytes = bytes },
        });
    }

    function RepeatCharacter(character, count) {
        local result = "";
        for (local index = 0; index < count; index++) result += character;
        return result;
    }

    function Base64Encode(value) {
        local characters = "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789+/";
        local result = "";
        for (local index = 0; index < value.len(); index += 3) {
            local first = value[index];
            local second = index + 1 < value.len() ? value[index + 1] : 0;
            local third = index + 2 < value.len() ? value[index + 2] : 0;
            local combined = first * 65536 + second * 256 + third;
            result += characters.slice(((combined / 262144).tointeger()) % 64, ((combined / 262144).tointeger()) % 64 + 1);
            result += characters.slice(((combined / 4096).tointeger()) % 64, ((combined / 4096).tointeger()) % 64 + 1);
            result += (index + 1 < value.len() ? characters.slice(((combined / 64).tointeger()) % 64, ((combined / 64).tointeger()) % 64 + 1) : "=");
            result += (index + 2 < value.len() ? characters.slice(combined % 64, combined % 64 + 1) : "=");
        }

        return result;
    }
}
