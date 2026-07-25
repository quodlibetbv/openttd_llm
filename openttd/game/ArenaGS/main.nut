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
    static MAX_SNAPSHOT_TOWNS = 8;
    static MAX_SNAPSHOT_INDUSTRIES = 8;
    static MAX_SNAPSHOT_STATIONS = 8;
    static MAX_SNAPSHOT_VEHICLES = 8;
    static MAX_SNAPSHOT_ROUTES = 8;
    static MAX_SNAPSHOT_PROJECTS = 8;
    static MAX_SNAPSHOT_EVENTS = 16;
    /* Bound the persisted A* frontier while allowing a route between two
     * valid perimeter stops to detour around terrain on the certified map. */
    static MAX_SEARCH_NODES = 4096;
    /* Each frontier expansion can invoke native test-mode road builds. Four
     * bounded probes per GameScript slice keep the dispatcher responsive while
     * allowing a short certified route to finish before its smoke timeout. */
    static MAX_SEARCH_STEPS_PER_TICK = 4;
    static MAX_PATH_TILES = 256;
    static MAX_PATH_REPLANS = 3;
    static MAX_STATION_SCAN_RADIUS = 24;
    static MAX_DEPOT_SCAN_RADIUS = 8;
    /* Test-mode placement probes call native build validation. Limit each
     * GameScript tick so a dense town cannot starve the AdminPort dispatcher. */
    static MAX_PLACEMENT_PROBES_PER_TICK = 1;
    static MAX_VERIFICATION_TICKS = 2220;
    static MAX_VEHICLE_START_TICKS = 222;
    static MAX_ROUTE_STALL_TICKS = 444;
    static MAX_FLEET_ADJUSTMENT_TICKS = 2220;
    static MAX_FLEET_ADJUSTMENT_HISTORY = 16;
    /* Four seconds at OpenTTD's 37 ticks/second leaves a bounded retry window
     * inside the bridge-smoke request timeout. */
    static TRANSFER_TIMEOUT_TICKS = 148;

    _active_run_id = null;
    _ledger = null;
    _ledger_order = null;
    _transfers = null;
    _outbound_transfers = null;
    _tick = 0;
    _message_sequence = 0;
    _finalized = false;
    _benchmark_company_id = null;
    _projects = null;
    _events = null;
    _event_sequence = 0;
    _snapshot_stage = "idle";
    _checkpoint_target = null;

    function Save() {
        return {
            protocol_version = ArenaGS.PROTOCOL_VERSION,
            active_run_id = this._active_run_id,
            ledger = this._ledger,
            ledger_order = this._ledger_order,
            finalized = this._finalized,
            message_sequence = this._message_sequence,
            benchmark_company_id = this._benchmark_company_id,
            projects = this._projects,
            events = this._events,
            event_sequence = this._event_sequence,
            tick = this._tick,
            checkpoint_target = this._checkpoint_target,
        };
    }

    function Load(version, data) {
        this._ledger = {};
        this._ledger_order = [];
        this._transfers = {};
        this._outbound_transfers = [];
        this._projects = [];
        this._events = [];
        this._tick = 0;
        this._checkpoint_target = null;
        if (data != null && typeof data == "table" && data.rawin("protocol_version") && data.protocol_version == ArenaGS.PROTOCOL_VERSION) {
            if (data.rawin("active_run_id")) this._active_run_id = data.active_run_id;
            if (data.rawin("ledger") && typeof data.ledger == "table") this._ledger = data.ledger;
            if (data.rawin("ledger_order") && typeof data.ledger_order == "array") this._ledger_order = data.ledger_order;
            if (data.rawin("finalized") && typeof data.finalized == "bool") this._finalized = data.finalized;
            if (data.rawin("message_sequence") && typeof data.message_sequence == "integer") this._message_sequence = data.message_sequence;
            if (data.rawin("benchmark_company_id") && typeof data.benchmark_company_id == "integer") this._benchmark_company_id = data.benchmark_company_id;
            if (data.rawin("projects") && typeof data.projects == "array") this._projects = data.projects;
            if (data.rawin("events") && typeof data.events == "array") this._events = data.events;
            if (data.rawin("event_sequence") && typeof data.event_sequence == "integer") this._event_sequence = data.event_sequence;
            if (data.rawin("tick") && typeof data.tick == "integer" && data.tick >= 0) this._tick = data.tick;
            if (data.rawin("checkpoint_target") && typeof data.checkpoint_target == "table") this._checkpoint_target = data.checkpoint_target;
        }

        /* Loading a fixed starting save must be observable while it remains paused. */
        GSLog.Info("ARENA_PHASE02_GAMESCRIPT_READY");
    }

    function Start() {
        if (this._ledger == null) this._ledger = {};
        if (this._ledger_order == null) this._ledger_order = [];
        if (this._transfers == null) this._transfers = {};
        if (this._outbound_transfers == null) this._outbound_transfers = [];
        if (this._projects == null) this._projects = [];
        if (this._events == null) this._events = [];

        /* Unlike Load(), this signal proves that the simulation has advanced
         * into the GameScript's cancellable event loop. */
        GSLog.Info("ARENA_PHASE03_GAMESCRIPT_ACTIVE");
        while (true) {
            this._tick += 1;
            this.ProcessEvents();
            this.DrainOutboundTransfers();
            this.ExpireTransfers();
            /* Project execution begins only after the provider decision and
             * action authorization boundary resumes the simulation. This
             * prevents game-side construction from advancing while a provider
             * call or schema-correction retry remains in flight. */
            if (!GSGame.IsPaused()) this.AdvanceProjects();
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
                        "observation_v1",
                        "road_executor_v1",
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
                    try {
                        this._snapshot_stage = "game_state";
                        local snapshot = this.SnapshotPayload();
                        if (from_chunk) {
                            snapshot.chunked_payload_bytes <- envelope.payload.chunked_payload_bytes;
                            snapshot.chunk_checksum <- envelope.payload.chunk_checksum;
                        }

                        /* Rich Phase 04 snapshots are intentionally allowed to
                         * exceed one AdminPort GameScript packet. Always use the
                         * versioned chunk envelope for snapshots, rather than
                         * silently dropping a response when GSAdmin.Send reaches
                         * its engine-specific direct-message limit. Snapshots are
                         * read-only, so a retry recomputes authoritative state. */
                        this._snapshot_stage = "chunk_encoding";
                        this.SendChunkedSnapshot(envelope, snapshot);
                        this._snapshot_stage = "idle";
                    } catch (exception) {
                        GSLog.Error("ARENA-PHASE04-SNAPSHOT-FAILED");
                        local stage = this._snapshot_stage == null ? "unknown" : this._snapshot_stage;
                        local detail = typeof exception == "string" ? this.LimitText(exception, 160, "native exception") : "native exception";
                        this._snapshot_stage = "idle";
                        this.SendError(envelope, "ARENA-PROTOCOL-INVALID-MESSAGE", "ArenaGS could not build a bounded authoritative snapshot during " + stage + " (" + detail + ").");
                    }
                }
                break;

            case "action_request":
                this.HandleActionRequest(envelope);
                break;

            case "camera_request":
                this.RecordAndSend(envelope, "camera_result", {
                    status = "deferred",
                    error_code = "ARENA-ACTION-CONSTRAINT-VIOLATION",
                    message = "Camera direction is not available until Phase 09.",
                });
                break;

            case "checkpoint_request":
                this.HandleCheckpointRequest(envelope);
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
            return false;
        }

        return true;
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
            game_state = this.BuildGameState(),
        };
    }

    /*
     * Phase 04 authoritative snapshot. The GameScript owns all game queries;
     * callers receive bounded IDs and public names, never internal search
     * state or a screen coordinate instruction stream.
     */
    function BuildGameState() {
        this._snapshot_stage = "company_resolve";
        local company_id = this.ResolveBenchmarkCompany();
        local company = null;
        local stations = [];
        local vehicles = [];
        if (company_id != null) {
            this._snapshot_stage = "company_mode";
            local company_mode = GSCompanyMode(company_id);
            this._snapshot_stage = "company_mode_valid";
            if (GSCompanyMode.IsValid()) {
                this._snapshot_stage = "company_collect";
                company = this.CollectCompany(company_id);
                this._snapshot_stage = "company_stations";
                stations = this.CollectStations();
                this._snapshot_stage = "company_vehicles";
                vehicles = this.CollectVehicles();
            }
        }

        if (company == null) {
            company = {
                company_id = 0,
                name = "No benchmark company",
                cash = 0,
                loan = 0,
                quarterly_income = 0,
                quarterly_expenses = 0,
                company_value = 0,
                performance_rating = 0,
            };
        }

        this._snapshot_stage = "towns";
        local towns = this.CollectTowns();
        this._snapshot_stage = "industries";
        local industries = this.CollectIndustries();
        this._snapshot_stage = "routes";
        local routes = this.CollectRoutes();
        this._snapshot_stage = "projects";
        local projects = this.CollectProjects();
        this._snapshot_stage = "events";
        local events = this.CollectEvents();

        return {
            schema_version = "1.0",
            game_date = this.GameDateText(),
            paused = GSGame.IsPaused(),
            /* Calendar day is the stable game-clock tick. The dispatcher loop
             * counter is deliberately not exposed because it advances while
             * no game-state change occurred and would break replay hashes. */
            game_tick = GSDate.GetCurrentDate(),
            company = company,
            towns = towns,
            industries = industries,
            stations = stations,
            vehicles = vehicles,
            routes = routes,
            projects = projects,
            events = events,
        };
    }

    function ResolveBenchmarkCompany() {
        if (this._benchmark_company_id != null && GSCompany.ResolveCompanyID(this._benchmark_company_id) == this._benchmark_company_id) {
            return this._benchmark_company_id;
        }

        for (local company = GSCompany.COMPANY_FIRST; company <= GSCompany.COMPANY_LAST; company++) {
            if (GSCompany.ResolveCompanyID(company) == company) {
                this._benchmark_company_id = company;
                return company;
            }
        }

        return null;
    }

    function CollectCompany(company_id) {
        local rating = GSCompany.GetQuarterlyPerformanceRating(company_id, GSCompany.EARLIEST_QUARTER);
        return {
            company_id = company_id,
            name = this.LimitText(GSCompany.GetName(company_id), 160, "Arena company"),
            cash = GSCompany.GetBankBalance(company_id),
            loan = GSCompany.GetLoanAmount(),
            quarterly_income = GSCompany.GetQuarterlyIncome(company_id, GSCompany.CURRENT_QUARTER),
            quarterly_expenses = GSCompany.GetQuarterlyExpenses(company_id, GSCompany.CURRENT_QUARTER),
            company_value = GSCompany.GetQuarterlyCompanyValue(company_id, GSCompany.CURRENT_QUARTER),
            performance_rating = rating < 0 ? 0 : rating,
        };
    }

    function CollectTowns() {
        local result = [];
        local towns = GSTownList();
        towns.Sort(GSList.SORT_BY_ITEM, GSList.SORT_ASCENDING);
        for (local town = towns.Begin(); !towns.IsEnd() && result.len() < ArenaGS.MAX_SNAPSHOT_TOWNS; town = towns.Next()) {
            if (!GSTown.IsValidTown(town)) continue;
            result.append({
                town_id = town,
                name = this.LimitText(GSTown.GetName(town), 160, "Unknown town"),
                population = this.NonNegative(GSTown.GetPopulation(town)),
                location = this.CoordinatePayload(GSTown.GetLocation(town)),
            });
        }

        return result;
    }

    function CollectIndustries() {
        local result = [];
        local industries = GSIndustryList();
        industries.Sort(GSList.SORT_BY_ITEM, GSList.SORT_ASCENDING);
        for (local industry = industries.Begin(); !industries.IsEnd() && result.len() < ArenaGS.MAX_SNAPSHOT_INDUSTRIES; industry = industries.Next()) {
            if (!GSIndustry.IsValidIndustry(industry)) continue;
            result.append({
                industry_id = industry,
                name = this.LimitText(GSIndustry.GetName(industry), 160, "Unknown industry"),
                location = this.CoordinatePayload(GSIndustry.GetLocation(industry)),
            });
        }

        return result;
    }

    function CollectStations() {
        local result = [];
        local stations = GSStationList(GSStation.STATION_ANY);
        stations.Sort(GSList.SORT_BY_ITEM, GSList.SORT_ASCENDING);
        for (local station = stations.Begin(); !stations.IsEnd() && result.len() < ArenaGS.MAX_SNAPSHOT_STATIONS; station = stations.Next()) {
            if (!GSStation.IsValidStation(station)) continue;
            /* OpenTTD 14.1 exposes the one-argument station vehicle list;
             * filter it locally rather than relying on the newer typed
             * overload so the certified runtime never enters an API shim. */
            local assigned = GSVehicleList_Station(station);
            local road_vehicle_count = 0;
            for (local vehicle = assigned.Begin(); !assigned.IsEnd(); vehicle = assigned.Next()) {
                if (GSVehicle.IsValidVehicle(vehicle) &&
                    GSVehicle.IsPrimaryVehicle(vehicle) &&
                    GSVehicle.GetVehicleType(vehicle) == GSVehicle.VT_ROAD) {
                    road_vehicle_count += 1;
                }
            }
            result.append({
                station_id = station,
                name = this.LimitText(GSBaseStation.GetName(station), 160, "Arena station"),
                location = this.CoordinatePayload(GSBaseStation.GetLocation(station)),
                vehicle_count = road_vehicle_count,
            });
        }

        return result;
    }

    function CollectVehicles() {
        local result = [];
        local vehicles = GSVehicleList();
        vehicles.Sort(GSList.SORT_BY_ITEM, GSList.SORT_ASCENDING);
        for (local vehicle = vehicles.Begin(); !vehicles.IsEnd() && result.len() < ArenaGS.MAX_SNAPSHOT_VEHICLES; vehicle = vehicles.Next()) {
            if (!GSVehicle.IsValidVehicle(vehicle) || !GSVehicle.IsPrimaryVehicle(vehicle)) continue;
            result.append({
                vehicle_id = vehicle,
                name = this.LimitText(GSVehicle.GetName(vehicle), 160, "Arena vehicle"),
                vehicle_type = this.VehicleTypeText(GSVehicle.GetVehicleType(vehicle)),
                state = this.VehicleStateText(GSVehicle.GetState(vehicle)),
                profit_last_year = GSVehicle.GetProfitLastYear(vehicle),
                location = this.CoordinatePayload(GSVehicle.GetLocation(vehicle)),
            });
        }

        return result;
    }

    function CollectRoutes() {
        local result = [];
        foreach (project in this._projects) {
            if (typeof project != "table" || !project.rawin("state") || (project.state != "completed" && project.state != "adjusting_fleet") ||
                !project.rawin("route_id") || !project.rawin("source_station_id") || !project.rawin("destination_station_id") || !project.rawin("vehicle_ids")) continue;
            result.append({
                route_id = project.route_id,
                action_id = project.action_id,
                source_station_id = project.source_station_id,
                destination_station_id = project.destination_station_id,
                cargo = "passengers",
                vehicle_ids = project.vehicle_ids,
                operational = project.vehicle_ids.len() > 0,
            });
            if (result.len() >= ArenaGS.MAX_SNAPSHOT_ROUTES) break;
        }

        return result;
    }

    function CollectProjects() {
        local result = [];
        foreach (project in this._projects) {
            if (typeof project != "table" || !project.rawin("project_id") || !project.rawin("action_id") || !project.rawin("state")) continue;
            local entry = {
                project_id = project.project_id,
                action_id = project.action_id,
                state = project.state,
                spent = project.rawin("spent") ? this.NonNegative(project.spent) : 0,
                maximum_budget = project.rawin("maximum_budget") ? this.Positive(project.maximum_budget, 1) : 1,
            };
            if (project.rawin("failure_code")) entry.failure_code <- project.failure_code;
            result.append(entry);
            if (result.len() >= ArenaGS.MAX_SNAPSHOT_PROJECTS) break;
        }

        return result;
    }

    function CollectEvents() {
        local result = [];
        local start = this._events.len() > ArenaGS.MAX_SNAPSHOT_EVENTS ? this._events.len() - ArenaGS.MAX_SNAPSHOT_EVENTS : 0;
        for (local index = start; index < this._events.len(); index++) result.append(this._events[index]);
        return result;
    }

    function CoordinatePayload(tile) {
        if (!GSMap.IsValidTile(tile)) return { x = 0, y = 0 };
        return { x = GSMap.GetTileX(tile), y = GSMap.GetTileY(tile) };
    }

    function VehicleTypeText(vehicle_type) {
        switch (vehicle_type) {
            case GSVehicle.VT_ROAD: return "road";
            case GSVehicle.VT_RAIL: return "rail";
            case GSVehicle.VT_WATER: return "water";
            case GSVehicle.VT_AIR: return "air";
        }

        return "road";
    }

    function VehicleStateText(state) {
        switch (state) {
            case GSVehicle.VS_RUNNING: return "running";
            case GSVehicle.VS_STOPPED: return "stopped";
            case GSVehicle.VS_IN_DEPOT: return "in_depot";
            case GSVehicle.VS_AT_STATION: return "at_station";
            case GSVehicle.VS_BROKEN: return "broken";
            case GSVehicle.VS_CRASHED: return "crashed";
        }

        return "stopped";
    }

    function GameDateText() {
        local date = GSDate.GetCurrentDate();
        return this.PadNumber(GSDate.GetYear(date), 4) + "-" + this.PadNumber(GSDate.GetMonth(date), 2) + "-" + this.PadNumber(GSDate.GetDayOfMonth(date), 2);
    }

    function PadNumber(value, width) {
        local text = value.tostring();
        while (text.len() < width) text = "0" + text;
        return text;
    }

    function LimitText(value, maximum_length, fallback) {
        if (value == null || typeof value != "string" || value.len() == 0) return fallback;
        local result = "";
        for (local index = 0; index < value.len() && result.len() < maximum_length; index++) {
            local character = value[index];
            if (character < 32 || character == 127) continue;
            if (character == 60) result += "(";
            else if (character == 62) result += ")";
            else result += value.slice(index, index + 1);
        }

        return result.len() == 0 ? fallback : result;
    }

    function NonNegative(value) {
        return typeof value == "integer" && value > 0 ? value : 0;
    }

    function Positive(value, fallback) {
        return typeof value == "integer" && value > 0 ? value : fallback;
    }

    /*
     * Checkpoints remain a supervisor-only protocol capability. Providers
     * cannot construct AdminPort envelopes and the model tool catalog does not
     * contain this request. It exists so a real process smoke can pause at an
     * exact persisted project stage, save, reload, and verify recovery without
     * relying on timing races between GameScript ticks.
     */
    function HandleCheckpointRequest(envelope) {
        local payload = envelope.payload;
        if (!this.HasExactFields(payload, ["project_id", "pause_after_state"]) ||
            typeof payload.project_id != "string" || !this.IsIdentifier(payload.project_id) ||
            typeof payload.pause_after_state != "string" || !this.IsCheckpointProjectState(payload.pause_after_state)) {
            this.RecordAndSend(envelope, "checkpoint_result", {
                status = "rejected",
                error_code = "ARENA-ACTION-CONSTRAINT-VIOLATION",
                message = "The supervisor checkpoint request must name one persisted project and one supported execution stage.",
                paused = GSGame.IsPaused(),
            });
            return;
        }

        local project = this.FindProjectById(payload.project_id);
        if (project == null) {
            this.RecordAndSend(envelope, "checkpoint_result", {
                status = "rejected",
                error_code = "ARENA-ACTION-CONSTRAINT-VIOLATION",
                message = "The supervisor checkpoint request does not reference a persisted project.",
                paused = GSGame.IsPaused(),
            });
            return;
        }

        this._checkpoint_target = {
            project_id = payload.project_id,
            state = payload.pause_after_state,
        };
        if (project.state == payload.pause_after_state) {
            this._checkpoint_target = null;
            GSGame.Pause();
            this.RecordAndSend(envelope, "checkpoint_result", {
                status = "paused",
                paused = true,
                message = "The trusted supervisor reached the requested persisted project checkpoint.",
            });
            return;
        }

        this.RecordAndSend(envelope, "checkpoint_result", {
            status = "armed",
            paused = GSGame.IsPaused(),
            message = "The trusted supervisor checkpoint is armed for the requested persisted project stage.",
        });
    }

    function IsCheckpointProjectState(state) {
        local states = {
            proposed = true,
            validating = true,
            surveying = true,
            building_infrastructure = true,
            buying_vehicles = true,
            configuring_orders = true,
            verifying = true,
        };
        return states.rawin(state);
    }

    function FindProjectById(project_id) {
        foreach (project in this._projects) {
            if (typeof project == "table" && project.rawin("project_id") && project.project_id == project_id) return project;
        }

        return null;
    }

    function PauseAtCheckpointIfReached(project) {
        if (this._checkpoint_target == null || typeof this._checkpoint_target != "table" ||
            !this._checkpoint_target.rawin("project_id") || !this._checkpoint_target.rawin("state") ||
            typeof this._checkpoint_target.project_id != "string" || typeof this._checkpoint_target.state != "string") {
            return;
        }

        if (project.project_id == this._checkpoint_target.project_id && project.state == this._checkpoint_target.state) {
            this._checkpoint_target = null;
            GSGame.Pause();
        }
    }

    function HandleActionRequest(envelope) {
        local action = this.ParseActionRequest(envelope);
        if (action == null) {
            /* Retain a bounded result for the Phase 03 malformed-action probe. */
            this.RecordAndSend(envelope, "action_result", {
                status = "rejected",
                error_code = "ARENA-ACTION-CONSTRAINT-VIOLATION",
                message = "The action request did not contain a supported typed road tool.",
            });
            return;
        }

        local existing = this.FindProjectByIdempotency(action.idempotency_key);
        if (existing != null) {
            this.RecordAndSend(envelope, "action_result", this.ActionResultPayload(
                action,
                "duplicate",
                null,
                "The idempotent route project already exists and was not duplicated.",
                { project_id = existing.project_id, state = existing.state }));
            return;
        }

        switch (action.tool) {
            case "inspect_company":
                this.HandleInspectCompany(envelope, action);
                return;

            case "list_opportunities":
                this.HandleListOpportunities(envelope, action);
                return;

            case "inspect_town":
                this.HandleInspectTown(envelope, action);
                return;

            case "inspect_industry":
                this.HandleInspectIndustry(envelope, action);
                return;

            case "build_transport_route":
                this.HandleBuildTransportRoute(envelope, action);
                return;

            case "take_loan":
            case "repay_loan":
                this.HandleLoanAction(envelope, action);
                return;

            case "wait":
                this.HandleWait(envelope, action);
                return;

            case "expand_route":
            case "reduce_route":
            case "replace_vehicles":
                this.HandleFleetChange(envelope, action);
                return;
        }

        this.RecordAndSend(envelope, "action_result", this.ActionResultPayload(
            action,
            "rejected",
            "ARENA-ACTION-CONSTRAINT-VIOLATION",
            "The typed road tool is not allowlisted by ArenaGS.",
            null));
    }

    function ParseActionRequest(envelope) {
        local payload = envelope.payload;
        local fields = ["action_id", "run_id", "decision_id", "correlation_id", "idempotency_key", "tool", "arguments"];
        if (!this.HasExactFields(payload, fields) ||
            typeof payload.action_id != "string" || !this.IsIdentifier(payload.action_id) ||
            typeof payload.run_id != "string" || payload.run_id != envelope.run_id ||
            typeof payload.decision_id != "string" || !this.IsIdentifier(payload.decision_id) ||
            typeof payload.correlation_id != "string" || payload.correlation_id != envelope.correlation_id ||
            typeof payload.idempotency_key != "string" || payload.idempotency_key != envelope.idempotency_key ||
            typeof payload.tool != "string" || !this.IsKnownRoadTool(payload.tool) ||
            typeof payload.arguments != "table" || !this.IsBoundedValue(payload.arguments, 0)) return null;
        return payload;
    }

    function HasExactFields(value, fields) {
        if (value == null || typeof value != "table") return false;
        foreach (field in fields) if (!value.rawin(field)) return false;
        local count = 0;
        foreach (key, item in value) {
            local known = false;
            foreach (field in fields) if (key == field) known = true;
            if (!known) return false;
            count += 1;
        }

        return count == fields.len();
    }

    function IsKnownRoadTool(tool) {
        local tools = {
            inspect_company = true,
            list_opportunities = true,
            inspect_town = true,
            inspect_industry = true,
            build_transport_route = true,
            expand_route = true,
            reduce_route = true,
            replace_vehicles = true,
            repay_loan = true,
            take_loan = true,
            wait = true,
        };
        return tools.rawin(tool);
    }

    function ActionResultPayload(action, status, error_code, message, data) {
        local result = {
            action_id = action.action_id,
            run_id = action.run_id,
            correlation_id = action.correlation_id,
            status = status,
            message = message,
        };
        if (error_code != null) result.error_code <- error_code;
        if (data != null) result.data <- data;
        return result;
    }

    function HandleInspectCompany(envelope, action) {
        if (!this.HasExactFields(action.arguments, [])) {
            this.RecordAndSend(envelope, "action_result", this.ActionResultPayload(action, "rejected", "ARENA-ACTION-CONSTRAINT-VIOLATION", "inspect_company does not accept arguments.", null));
            return;
        }

        local company_id = this.ResolveBenchmarkCompany();
        if (company_id == null) {
            this.RecordAndSend(envelope, "action_result", this.ActionResultPayload(action, "failed", "ARENA-ACTION-CONSTRAINT-VIOLATION", "No benchmark company is available.", null));
            return;
        }

        local mode = GSCompanyMode(company_id);
        this.RecordAndSend(envelope, "action_result", this.ActionResultPayload(action, "completed", null, "The company summary was read from the authoritative game state.", this.CollectCompany(company_id)));
    }

    function HandleListOpportunities(envelope, action) {
        if (!this.HasExactFields(action.arguments, [])) {
            this.RecordAndSend(envelope, "action_result", this.ActionResultPayload(action, "rejected", "ARENA-ACTION-CONSTRAINT-VIOLATION", "list_opportunities does not accept arguments.", null));
            return;
        }

        this.RecordAndSend(envelope, "action_result", this.ActionResultPayload(action, "completed", null, "The bounded town and industry opportunity set was read from the authoritative game state.", {
            town_count = GSTown.GetTownCount(),
            industry_count = GSIndustry.GetIndustryCount(),
        }));
    }

    function HandleInspectTown(envelope, action) {
        local town_id = this.RequiredEntityArgument(action.arguments, "town_id");
        if (town_id == null || !GSTown.IsValidTown(town_id)) {
            this.RecordAndSend(envelope, "action_result", this.ActionResultPayload(action, "rejected", "ARENA-ACTION-CONSTRAINT-VIOLATION", "The town is not present in the authoritative game state.", null));
            return;
        }

        this.RecordAndSend(envelope, "action_result", this.ActionResultPayload(action, "completed", null, "The town summary was read from the authoritative game state.", {
            town_id = town_id,
            name = this.LimitText(GSTown.GetName(town_id), 160, "Unknown town"),
            population = this.NonNegative(GSTown.GetPopulation(town_id)),
            location = this.CoordinatePayload(GSTown.GetLocation(town_id)),
        }));
    }

    function HandleInspectIndustry(envelope, action) {
        local industry_id = this.RequiredEntityArgument(action.arguments, "industry_id");
        if (industry_id == null || !GSIndustry.IsValidIndustry(industry_id)) {
            this.RecordAndSend(envelope, "action_result", this.ActionResultPayload(action, "rejected", "ARENA-ACTION-CONSTRAINT-VIOLATION", "The industry is not present in the authoritative game state.", null));
            return;
        }

        this.RecordAndSend(envelope, "action_result", this.ActionResultPayload(action, "completed", null, "The industry summary was read from the authoritative game state.", {
            industry_id = industry_id,
            name = this.LimitText(GSIndustry.GetName(industry_id), 160, "Unknown industry"),
            location = this.CoordinatePayload(GSIndustry.GetLocation(industry_id)),
        }));
    }

    function RequiredEntityArgument(arguments, field) {
        if (!this.HasExactFields(arguments, [field]) || typeof arguments[field] != "integer" || arguments[field] < 0) return null;
        return arguments[field];
    }

    function HandleWait(envelope, action) {
        if (!this.HasExactFields(action.arguments, ["game_days"]) || typeof action.arguments.game_days != "integer" || action.arguments.game_days < 1 || action.arguments.game_days > 365) {
            this.RecordAndSend(envelope, "action_result", this.ActionResultPayload(action, "rejected", "ARENA-ACTION-CONSTRAINT-VIOLATION", "wait requires a bounded game_days integer.", null));
            return;
        }

        this.RecordAndSend(envelope, "action_result", this.ActionResultPayload(action, "completed", null, "No construction was requested; the orchestrator may resume until the declared review interval.", {
            game_days = action.arguments.game_days,
        }));
    }

    function HandleLoanAction(envelope, action) {
        if (!this.HasExactFields(action.arguments, ["amount"]) || typeof action.arguments.amount != "integer" || action.arguments.amount <= 0) {
            this.RecordAndSend(envelope, "action_result", this.ActionResultPayload(action, "rejected", "ARENA-ACTION-CONSTRAINT-VIOLATION", "The finance tool requires one positive integer amount.", null));
            return;
        }

        local company_id = this.ResolveBenchmarkCompany();
        if (company_id == null) {
            this.RecordAndSend(envelope, "action_result", this.ActionResultPayload(action, "failed", "ARENA-ACTION-CONSTRAINT-VIOLATION", "No benchmark company is available for the finance action.", null));
            return;
        }

        local mode = GSCompanyMode(company_id);
        local interval = GSCompany.GetLoanInterval();
        local current = GSCompany.GetLoanAmount();
        local amount = action.arguments.amount;
        if (interval <= 0 || amount % interval != 0) {
            this.RecordAndSend(envelope, "action_result", this.ActionResultPayload(action, "rejected", "ARENA-ACTION-CONSTRAINT-VIOLATION", "The finance amount must align to the current company loan interval.", null));
            return;
        }

        local target = action.tool == "take_loan" ? current + amount : current - amount;
        if (target < 0 || target > GSCompany.GetMaxLoanAmount() || !GSCompany.SetLoanAmount(target)) {
            this.RecordAndSend(envelope, "action_result", this.ActionResultPayload(action, "failed", "ARENA-ACTION-CONSTRAINT-VIOLATION", "The game rejected the requested loan adjustment.", null));
            return;
        }

        this.RecordEvent("ARENA-FINANCE-UPDATED", ["company-" + company_id], "A bounded company loan adjustment completed.", action.correlation_id);
        this.RecordAndSend(envelope, "action_result", this.ActionResultPayload(action, "completed", null, "The company loan was adjusted through the native company API.", {
            loan = GSCompany.GetLoanAmount(),
        }));
    }

    function HandleBuildTransportRoute(envelope, action) {
        local request = this.ValidateRouteBuildArguments(action.arguments);
        if (request == null) {
            this.RecordAndSend(envelope, "action_result", this.ActionResultPayload(action, "rejected", "ARENA-ACTION-CONSTRAINT-VIOLATION", "The route request has invalid typed arguments or an invalid budget.", null));
            return;
        }

        local company_id = this.ResolveBenchmarkCompany();
        if (company_id == null || !GSTown.IsValidTown(request.source_town_id) || !GSTown.IsValidTown(request.destination_town_id)) {
            this.RecordAndSend(envelope, "action_result", this.ActionResultPayload(action, "rejected", "ARENA-ACTION-CONSTRAINT-VIOLATION", "The route request does not reference two live towns and one benchmark company.", null));
            return;
        }

        if (GSCompany.GetBankBalance(company_id) < request.maximum_budget) {
            this.RecordAndSend(envelope, "action_result", this.ActionResultPayload(action, "rejected", "ARENA-ACTION-INSUFFICIENT-FUNDS", "The declared route budget exceeds the current company cash.", null));
            return;
        }

        local project = {
            project_id = this.ProjectIdFor(action.action_id),
            action_id = action.action_id,
            decision_id = action.decision_id,
            correlation_id = action.correlation_id,
            idempotency_key = action.idempotency_key,
            company_id = company_id,
            source_town_id = request.source_town_id,
            destination_town_id = request.destination_town_id,
            cargo = "passengers",
            initial_vehicle_count = request.initial_vehicle_count,
            maximum_budget = request.maximum_budget,
            spent = 0,
            state = "proposed",
            vehicle_ids = [],
            route_id = this.RouteIdFor(action.action_id),
        };
        this._projects.append(project);
        this.RecordEvent("ARENA-PROJECT-PROPOSED", [project.project_id], "A typed passenger road project was accepted for deterministic execution.", action.correlation_id);
        this.RecordAndSend(envelope, "action_result", this.ActionResultPayload(action, "accepted", null, "The passenger road project was accepted and will advance only through persisted GameScript stages.", {
            project_id = project.project_id,
            state = project.state,
        }));
    }

    function ValidateRouteBuildArguments(arguments) {
        local fields = ["mode", "source_town_id", "destination_town_id", "cargo", "initial_vehicle_count", "maximum_budget"];
        if (!this.HasExactFields(arguments, fields) ||
            typeof arguments.mode != "string" || arguments.mode != "road" ||
            typeof arguments.source_town_id != "integer" || arguments.source_town_id < 0 ||
            typeof arguments.destination_town_id != "integer" || arguments.destination_town_id < 0 ||
            arguments.source_town_id == arguments.destination_town_id ||
            typeof arguments.cargo != "string" || arguments.cargo != "passengers" ||
            typeof arguments.initial_vehicle_count != "integer" || arguments.initial_vehicle_count < 1 || arguments.initial_vehicle_count > 8 ||
            typeof arguments.maximum_budget != "integer" || arguments.maximum_budget < 1 || arguments.maximum_budget > 2000000000) return null;
        return arguments;
    }

    function HandleFleetChange(envelope, action) {
        local request = this.ValidateFleetChangeArguments(action.tool, action.arguments);
        if (request == null) {
            this.RecordAndSend(envelope, "action_result", this.ActionResultPayload(action, "rejected", "ARENA-ACTION-CONSTRAINT-VIOLATION", "The fleet-change request has invalid typed arguments, target count, or declared purchase budget.", null));
            return;
        }

        local project = this.FindOperationalRouteProject(request.route_id);
        if (project == null || project.rawin("fleet_adjustment")) {
            this.RecordAndSend(envelope, "action_result", this.ActionResultPayload(action, "rejected", "ARENA-ACTION-CONSTRAINT-VIOLATION", "The requested route is not operational or already has one persisted fleet adjustment.", null));
            return;
        }

        /* Vehicle availability and build-test commands are company-scoped in
         * the OpenTTD Script API. Project ticks already enter this context;
         * the accepted-action preflight runs immediately on the AdminPort
         * dispatcher, so it must establish the same trusted company boundary
         * before estimating a compatible purchase. */
        if (!project.rawin("company_id")) {
            this.RecordAndSend(envelope, "action_result", this.ActionResultPayload(action, "rejected", "ARENA-ACTION-CONSTRAINT-VIOLATION", "The persisted route does not retain a benchmark company for the fleet adjustment.", null));
            return;
        }

        local mode = GSCompanyMode(project.company_id);
        if (!GSCompanyMode.IsValid()) {
            this.RecordAndSend(envelope, "action_result", this.ActionResultPayload(action, "failed", "ARENA-ACTION-CONSTRAINT-VIOLATION", "The route fleet adjustment could not enter its benchmark company context.", null));
            return;
        }

        local current_count = project.vehicle_ids.len();
        if ((action.tool == "expand_route" && request.vehicle_count <= current_count) ||
            (action.tool == "reduce_route" && request.vehicle_count >= current_count)) {
            this.RecordAndSend(envelope, "action_result", this.ActionResultPayload(action, "rejected", "ARENA-ACTION-CONSTRAINT-VIOLATION", "The requested fleet target does not change the current operational route in the selected direction.", null));
            return;
        }

        if (action.tool != "reduce_route") {
            local purchase_count = action.tool == "expand_route" ? request.vehicle_count - current_count : request.vehicle_count;
            local preflight = this.EstimateFleetPurchase(project, purchase_count);
            if (!preflight.success) {
                this.RecordAndSend(envelope, "action_result", this.ActionResultPayload(action, "rejected", "ARENA-ACTION-VEHICLE-UNSUITABLE", "The route does not expose a compatible vehicle purchase plan for the requested fleet change.", null));
                return;
            }

            if (preflight.cost > request.maximum_budget || GSCompany.GetBankBalance(project.company_id) < preflight.cost) {
                this.RecordAndSend(envelope, "action_result", this.ActionResultPayload(action, "rejected", "ARENA-ACTION-INSUFFICIENT-FUNDS", "The declared fleet-change budget exceeds the current affordable native vehicle plan.", null));
                return;
            }
        }

        local adjustment = {
            action_id = action.action_id,
            correlation_id = action.correlation_id,
            idempotency_key = action.idempotency_key,
            tool = action.tool,
            target_count = request.vehicle_count,
            maximum_budget = action.tool == "reduce_route" ? 0 : request.maximum_budget,
            spent = 0,
            removal_vehicle_ids = [],
            depot_requests = {},
            wait_ticks = 0,
        };
        if (action.tool == "reduce_route") {
            for (local index = request.vehicle_count; index < current_count; index++) adjustment.removal_vehicle_ids.append(project.vehicle_ids[index]);
        } else if (action.tool == "replace_vehicles") {
            foreach (vehicle_id in project.vehicle_ids) adjustment.removal_vehicle_ids.append(vehicle_id);
        }

        project.fleet_adjustment <- adjustment;
        project.state = "adjusting_fleet";
        this.RecordEvent("ARENA-ROUTE-FLEET-ADJUSTING", [project.project_id, project.route_id], "A bounded fleet adjustment was accepted and will execute through persisted native GameScript stages.", action.correlation_id);
        this.RecordAndSend(envelope, "action_result", this.ActionResultPayload(action, "accepted", null, "The requested route fleet adjustment was accepted and will advance only through persisted GameScript stages.", {
            route_id = project.route_id,
            target_vehicle_count = request.vehicle_count,
            state = project.state,
        }));
    }

    function ValidateFleetChangeArguments(tool, arguments) {
        local purchases = tool == "expand_route" || tool == "replace_vehicles";
        local expected = purchases ? ["route_id", "vehicle_count", "maximum_budget"] : ["route_id", "vehicle_count"];
        if (!this.HasExactFields(arguments, expected) ||
            typeof arguments.route_id != "string" || !this.IsIdentifier(arguments.route_id) ||
            typeof arguments.vehicle_count != "integer" || arguments.vehicle_count < 1 || arguments.vehicle_count > 8) return null;
        if (purchases && (typeof arguments.maximum_budget != "integer" || arguments.maximum_budget < 1 || arguments.maximum_budget > 2000000000)) return null;
        return arguments;
    }

    function FindOperationalRouteProject(route_id) {
        foreach (project in this._projects) {
            if (typeof project == "table" && project.rawin("state") && project.state == "completed" &&
                project.rawin("route_id") && project.route_id == route_id && project.rawin("vehicle_ids") && project.vehicle_ids.len() > 0) return project;
        }

        return null;
    }

    function EstimateFleetPurchase(project, count) {
        if (count < 1 || !project.rawin("engine_id") || !project.rawin("cargo_id") || !project.rawin("depot_tile")) return { success = false, cost = 0 };
        local estimate = this.EstimateVehicle(project.depot_tile, project.engine_id, project.cargo_id);
        if (!estimate.success || estimate.cost < 0 || estimate.cost > 2000000000 / count) return { success = false, cost = 0 };
        return { success = true, cost = estimate.cost * count };
    }

    function ProjectIdFor(action_id) {
        local suffix = action_id.len() > 116 ? action_id.slice(0, 116) : action_id;
        return "project-" + suffix;
    }

    function RouteIdFor(action_id) {
        local suffix = action_id.len() > 120 ? action_id.slice(0, 120) : action_id;
        return "route-" + suffix;
    }

    function FindProjectByIdempotency(idempotency_key) {
        foreach (project in this._projects) {
            if (typeof project != "table") continue;
            if (project.rawin("idempotency_key") && project.idempotency_key == idempotency_key) return project;
            if (project.rawin("fleet_adjustment") && typeof project.fleet_adjustment == "table" &&
                project.fleet_adjustment.rawin("idempotency_key") && project.fleet_adjustment.idempotency_key == idempotency_key) return project;
            if (project.rawin("fleet_adjustment_history") && typeof project.fleet_adjustment_history == "array") {
                foreach (entry in project.fleet_adjustment_history) {
                    if (typeof entry == "table" && entry.rawin("idempotency_key") && entry.idempotency_key == idempotency_key) return project;
                }
            }
        }

        return null;
    }

    function RecordEvent(event_code, entity_ids, summary, correlation_id) {
        this._event_sequence += 1;
        local entry = {
            event_id = "event-" + this._event_sequence,
            event_code = event_code,
            game_date = this.GameDateText(),
            entity_ids = entity_ids,
            public_summary = this.LimitText(summary, 500, "Arena event recorded."),
        };
        if (correlation_id != null && this.IsIdentifier(correlation_id)) entry.correlation_id <- correlation_id;
        this._events.append(entry);
        while (this._events.len() > 64) this._events.remove(0);
    }

    function AdvanceProjects() {
        foreach (project in this._projects) {
            if (typeof project != "table" || !project.rawin("state") || project.state == "completed" || project.state == "failed") continue;
            /* A native command precondition must never terminate the trusted
             * AdminPort dispatcher. Preserve the project, stop further work,
             * and surface a classified failure on the next snapshot instead. */
            try {
                this.AdvanceProject(project);
                this.PauseAtCheckpointIfReached(project);
                if (GSGame.IsPaused()) return;
            } catch (exception) {
                local failed_stage = typeof project.state == "string" ? project.state : "unknown";
                GSLog.Error("ARENA-PROJECT-EXECUTION-FAILED");
                this.BeginRecovery(project, "ARENA-ACTION-CONSTRAINT-VIOLATION", "A native executor precondition failed during the " + failed_stage + " stage; the project stopped before additional construction.");
            }
        }
    }

    function AdvanceProject(project) {
        if (project.state == "recovering") {
            project.state = "failed";
            this.RecordEvent("ARENA-PROJECT-FAILED", [project.project_id], "The project stopped safely after deterministic recovery; valid partial assets were retained for inspection.", project.correlation_id);
            return;
        }

        if (!project.rawin("company_id") || GSCompany.ResolveCompanyID(project.company_id) != project.company_id) {
            this.BeginRecovery(project, "ARENA-ACTION-CONSTRAINT-VIOLATION", "The benchmark company is no longer available for the project.");
            return;
        }

        local mode = GSCompanyMode(project.company_id);
        if (!GSCompanyMode.IsValid()) {
            this.BeginRecovery(project, "ARENA-ACTION-CONSTRAINT-VIOLATION", "The project could not enter the benchmark company context.");
            return;
        }

        /* The active road type is native API process state rather than a
         * serialized project field. Re-establish it before every persisted
         * project slice so a checkpoint reload resumes surveying, building,
         * and vehicle work with the same ordinary-road semantics. */
        if (!GSRoad.IsRoadTypeAvailable(GSRoad.ROADTYPE_ROAD)) {
            this.BeginRecovery(project, "ARENA-ACTION-PATH-NOT-FOUND", "The persisted road project cannot resume because ordinary roads are no longer available.");
            return;
        }

        GSRoad.SetCurrentRoadType(GSRoad.ROADTYPE_ROAD);

        switch (project.state) {
            case "proposed":
                project.state = "validating";
                this.RecordEvent("ARENA-PROJECT-VALIDATING", [project.project_id], "The accepted project entered deterministic validation.", project.correlation_id);
                break;

            case "validating":
                this.BeginProjectSurvey(project);
                break;

            case "surveying":
                this.AdvanceProjectSurvey(project);
                break;

            case "building_infrastructure":
                this.AdvanceInfrastructure(project);
                break;

            case "buying_vehicles":
                this.AdvanceVehiclePurchase(project);
                break;

            case "configuring_orders":
                this.AdvanceOrderConfiguration(project);
                break;

            case "verifying":
                this.AdvanceVerification(project);
                break;

            case "adjusting_fleet":
                this.AdvanceFleetAdjustment(project);
                break;
        }
    }

    function AdvanceFleetAdjustment(project) {
        if (!project.rawin("fleet_adjustment") || typeof project.fleet_adjustment != "table") {
            project.state = "completed";
            return;
        }

        local adjustment = project.fleet_adjustment;
        if (!adjustment.rawin("tool") || !adjustment.rawin("target_count") ||
            typeof adjustment.tool != "string" || typeof adjustment.target_count != "integer") {
            this.FailFleetAdjustment(project, "ARENA-ACTION-CONSTRAINT-VIOLATION", "The persisted fleet adjustment state is invalid and was stopped safely.");
            return;
        }

        if (this.OperationalRouteTopologyError(project) != null) {
            this.FailFleetAdjustment(project, "ARENA-ACTION-PATH-NOT-FOUND", "The route no longer exposes the native topology required for a fleet adjustment.");
            return;
        }

        if (adjustment.tool == "expand_route") {
            if (project.vehicle_ids.len() >= adjustment.target_count) {
                this.CompleteFleetAdjustment(project);
                return;
            }

            this.AdvanceFleetPurchase(project, adjustment);
            return;
        }

        if (adjustment.tool == "reduce_route") {
            if (adjustment.removal_vehicle_ids.len() == 0) {
                this.CompleteFleetAdjustment(project);
                return;
            }

            this.AdvanceFleetRemoval(project, adjustment, false);
            return;
        }

        if (adjustment.tool == "replace_vehicles") {
            if (adjustment.removal_vehicle_ids.len() > 0) {
                this.AdvanceFleetRemoval(project, adjustment, true);
                return;
            }

            if (project.vehicle_ids.len() >= adjustment.target_count) {
                this.CompleteFleetAdjustment(project);
                return;
            }

            this.AdvanceFleetPurchase(project, adjustment);
            return;
        }

        this.FailFleetAdjustment(project, "ARENA-ACTION-CONSTRAINT-VIOLATION", "The persisted fleet adjustment requested an unsupported typed operation.");
    }

    function AdvanceFleetPurchase(project, adjustment) {
        local estimate = this.EstimateVehicle(project.depot_tile, project.engine_id, project.cargo_id);
        if (!estimate.success) {
            this.FailFleetAdjustment(project, "ARENA-ACTION-VEHICLE-UNSUITABLE", "The route's compatible vehicle is no longer buildable at its depot.");
            return;
        }

        if (!this.CanSpendFleetAdjustment(project, adjustment, estimate.cost)) {
            this.FailFleetAdjustment(project, "ARENA-ACTION-BUDGET-EXCEEDED", "The next native fleet purchase would exceed the adjustment budget or current company cash.");
            return;
        }

        local accounting = GSAccounting();
        local vehicle_id = GSVehicle.BuildVehicleWithRefit(project.depot_tile, project.engine_id, project.cargo_id);
        local actual_cost = this.AbsoluteCost(accounting.GetCosts());
        if (!GSVehicle.IsValidVehicle(vehicle_id)) {
            this.FailFleetAdjustment(project, "ARENA-ACTION-VEHICLE-UNSUITABLE", "The native game rejected a compatible route vehicle purchase.");
            return;
        }

        if (actual_cost > estimate.cost || adjustment.spent + actual_cost > adjustment.maximum_budget) {
            /* A newly built road vehicle is stopped in its depot. Roll it back
             * before reporting the bound violation so an expand action never
             * leaves an untracked or over-budget extra vehicle. If OpenTTD
             * rejects that safe rollback, retain the ID for deterministic
             * recovery rather than pretending the project stayed unchanged. */
            if (GSVehicle.IsStoppedInDepot(vehicle_id) && GSVehicle.SellVehicle(vehicle_id)) {
                this.FailFleetAdjustment(project, "ARENA-ACTION-BUDGET-EXCEEDED", "A native fleet purchase changed cost after preflight and was rolled back before exceeding the declared adjustment budget.");
            } else {
                project.vehicle_ids.append(vehicle_id);
                this.FailFleetAdjustment(project, "ARENA-ACTION-BUDGET-EXCEEDED", "A native fleet purchase changed cost after preflight and the newly created vehicle could not be safely rolled back.");
            }
            return;
        }

        project.vehicle_ids.append(vehicle_id);
        adjustment.spent += actual_cost;
        if (!this.ConfigureRouteVehicle(project, vehicle_id)) {
            this.FailFleetAdjustment(project, "ARENA-ACTION-ORDER-INVALID", "A newly purchased route vehicle could not receive its required passenger station orders.");
            return;
        }

        this.RecordEvent("ARENA-VEHICLE-CREATED", [project.project_id, project.route_id, "vehicle-" + vehicle_id], "A compatible vehicle was added through the persisted route fleet adjustment.", adjustment.correlation_id);
    }

    function AdvanceFleetRemoval(project, adjustment, replacing) {
        local vehicle_id = adjustment.removal_vehicle_ids[0];
        if (!GSVehicle.IsValidVehicle(vehicle_id)) {
            this.RemoveVehicleFromProject(project, vehicle_id);
            adjustment.removal_vehicle_ids.remove(0);
            return;
        }

        if (!GSVehicle.IsStoppedInDepot(vehicle_id)) {
            if (!adjustment.depot_requests.rawin(vehicle_id)) {
                if (!GSVehicle.SendVehicleToDepot(vehicle_id)) {
                    this.FailFleetAdjustment(project, "ARENA-ACTION-ORDER-INVALID", "A route vehicle could not be sent to its depot for the requested fleet adjustment.");
                    return;
                }

                adjustment.depot_requests[vehicle_id] <- true;
                this.RecordEvent("ARENA-ROUTE-FLEET-PROGRESS", [project.project_id, project.route_id, "vehicle-" + vehicle_id], "A route vehicle was sent to its depot for a bounded fleet adjustment.", adjustment.correlation_id);
            }

            adjustment.wait_ticks += 1;
            if (adjustment.wait_ticks > ArenaGS.MAX_FLEET_ADJUSTMENT_TICKS) {
                this.FailFleetAdjustment(project, "ARENA-ACTION-VERIFICATION-TIMED-OUT", "A route vehicle did not reach its depot before the bounded fleet-adjustment deadline.");
            }

            return;
        }

        if (!GSVehicle.SellVehicle(vehicle_id)) {
            this.FailFleetAdjustment(project, "ARENA-ACTION-VEHICLE-UNSUITABLE", "The native game rejected sale of a stopped depot vehicle during the fleet adjustment.");
            return;
        }

        this.RemoveVehicleFromProject(project, vehicle_id);
        adjustment.removal_vehicle_ids.remove(0);
        adjustment.wait_ticks = 0;
        local summary = replacing
            ? "A route vehicle was removed before deterministic replacement."
            : "A route vehicle was removed to reduce the active fleet.";
        this.RecordEvent("ARENA-VEHICLE-REMOVED", [project.project_id, project.route_id, "vehicle-" + vehicle_id], summary, adjustment.correlation_id);
    }

    function ConfigureRouteVehicle(project, vehicle_id) {
        if (!GSVehicle.IsValidVehicle(vehicle_id) || !GSVehicle.IsPrimaryVehicle(vehicle_id)) return false;
        local order_count = GSOrder.GetOrderCount(vehicle_id);
        if (order_count == 0) {
            if (!GSOrder.AppendOrder(vehicle_id, project.source_station_tile, GSOrder.OF_NONE) ||
                !GSOrder.AppendOrder(vehicle_id, project.destination_station_tile, GSOrder.OF_NONE)) return false;
            order_count = GSOrder.GetOrderCount(vehicle_id);
        }

        if (order_count < 2) return false;
        local state = GSVehicle.GetState(vehicle_id);
        return (state != GSVehicle.VS_STOPPED && state != GSVehicle.VS_IN_DEPOT) || GSVehicle.StartStopVehicle(vehicle_id);
    }

    function CanSpendFleetAdjustment(project, adjustment, expected_cost) {
        return typeof expected_cost == "integer" && expected_cost >= 0 &&
            adjustment.spent + expected_cost <= adjustment.maximum_budget &&
            GSCompany.GetBankBalance(project.company_id) >= expected_cost;
    }

    function RemoveVehicleFromProject(project, vehicle_id) {
        for (local index = project.vehicle_ids.len() - 1; index >= 0; index--) {
            if (project.vehicle_ids[index] == vehicle_id) project.vehicle_ids.remove(index);
        }
    }

    function CompleteFleetAdjustment(project) {
        local adjustment = project.fleet_adjustment;
        project.initial_vehicle_count = project.vehicle_ids.len();
        this.RecordFleetAdjustmentOutcome(project, adjustment, "completed", null);
        project.rawdelete("fleet_adjustment");
        project.state = "completed";
        this.RecordEvent("ARENA-ROUTE-FLEET-UPDATED", [project.project_id, project.route_id], "The persisted route fleet adjustment completed within its declared native budget boundary.", adjustment.correlation_id);
    }

    function FailFleetAdjustment(project, error_code, message) {
        local adjustment = project.fleet_adjustment;
        this.RecordFleetAdjustmentOutcome(project, adjustment, "failed", error_code);
        this.RecordEvent("ARENA-ROUTE-FLEET-FAILED", [project.project_id, project.route_id], this.LimitText(message, 500, "The fleet adjustment stopped safely."), adjustment.rawin("correlation_id") ? adjustment.correlation_id : project.correlation_id);
        project.rawdelete("fleet_adjustment");
        if (project.vehicle_ids.len() == 0) {
            this.BeginRecovery(project, error_code, message);
            return;
        }

        project.state = "completed";
    }

    function RecordFleetAdjustmentOutcome(project, adjustment, outcome, error_code) {
        if (typeof adjustment != "table" || !adjustment.rawin("idempotency_key")) return;
        if (!project.rawin("fleet_adjustment_history") || typeof project.fleet_adjustment_history != "array") project.fleet_adjustment_history <- [];
        local entry = {
            action_id = adjustment.rawin("action_id") ? adjustment.action_id : "unknown",
            idempotency_key = adjustment.idempotency_key,
            outcome = outcome,
        };
        if (error_code != null) entry.error_code <- error_code;
        project.fleet_adjustment_history.append(entry);
        while (project.fleet_adjustment_history.len() > ArenaGS.MAX_FLEET_ADJUSTMENT_HISTORY) project.fleet_adjustment_history.remove(0);
    }

    function BeginRecovery(project, error_code, message) {
        if (project.state == "recovering" || project.state == "failed") return;
        project.failure_code <- error_code;
        project.failure_message <- this.LimitText(message, 500, "The project failed safely.");
        project.state = "recovering";
        this.RecordEvent("ARENA-PROJECT-RECOVERING", [project.project_id], project.failure_message, project.correlation_id);
    }

    function BeginProjectSurvey(project) {
        if (!GSRoad.IsRoadTypeAvailable(GSRoad.ROADTYPE_ROAD)) {
            this.BeginRecovery(project, "ARENA-ACTION-PATH-NOT-FOUND", "The certified map does not expose a buildable road type for the requested passenger route.");
            return;
        }

        /* The GameScript road API is stateful; explicitly select ordinary
         * roads before every persisted project so test-mode and execute-mode
         * station, depot, and segment checks use the same native road type. */
        GSRoad.SetCurrentRoadType(GSRoad.ROADTYPE_ROAD);
        local cargo_id = this.FindPassengerCargo();
        if (cargo_id == null) {
            this.BeginRecovery(project, "ARENA-ACTION-VEHICLE-UNSUITABLE", "No passenger cargo is available in this game configuration.");
            return;
        }

        project.cargo_id <- cargo_id;
        this.BeginStationPlacementSearch(project, "source", project.source_town_id);
        project.state = "surveying";
        this.RecordEvent("ARENA-PROJECT-SURVEYING", [project.project_id], "The project began bounded incremental station and depot placement surveys.", project.correlation_id);
    }

    function FindPassengerCargo() {
        local cargoes = GSCargoList();
        cargoes.Sort(GSList.SORT_BY_ITEM, GSList.SORT_ASCENDING);
        for (local cargo = cargoes.Begin(); !cargoes.IsEnd(); cargo = cargoes.Next()) {
            if (GSCargo.IsValidCargo(cargo) && GSCargo.GetTownEffect(cargo) == GSCargo.TE_PASSENGERS) return cargo;
        }

        return null;
    }

    function BeginStationPlacementSearch(project, stage, town_id) {
        local center = GSTown.GetLocation(town_id);
        if (!GSMap.IsValidTile(center)) {
            this.BeginRecovery(project, "ARENA-ACTION-STATION-PLACEMENT-FAILED", "The selected town has no valid station-placement centre tile.");
            return;
        }

        this.BeginPlacementSearch(project, stage, "station", GSMap.GetTileX(center), GSMap.GetTileY(center), ArenaGS.MAX_STATION_SCAN_RADIUS);
    }

    function BeginDepotPlacementSearch(project) {
        if (!GSMap.IsValidTile(project.source_station_front)) {
            this.BeginRecovery(project, "ARENA-ACTION-STATION-PLACEMENT-FAILED", "The source station has no valid depot-placement anchor.");
            return;
        }

        this.BeginPlacementSearch(project, "depot", "depot", GSMap.GetTileX(project.source_station_front), GSMap.GetTileY(project.source_station_front), ArenaGS.MAX_DEPOT_SCAN_RADIUS);
    }

    function BeginPlacementSearch(project, stage, kind, center_x, center_y, radius) {
        local side = radius * 2 + 1;
        project.placement_search <- {
            stage = stage,
            kind = kind,
            center_x = center_x,
            center_y = center_y,
            radius = radius,
            side = side,
            index = 0,
            maximum = side * side * 4,
            last_error = "none",
        };
    }

    function AdvancePlacementSearch(project) {
        local search = project.placement_search;
        for (local probe = 0; probe < ArenaGS.MAX_PLACEMENT_PROBES_PER_TICK; probe++) {
            if (search.index >= search.maximum) {
                this.BeginRecovery(project, "ARENA-ACTION-STATION-PLACEMENT-FAILED", "The bounded " + search.kind + " placement search exhausted its deterministic candidates (last native error=" + this.LimitText(search.last_error, 160, "unknown") + ").");
                return;
            }

            local candidate = search.index;
            search.index += 1;
            local tile_index = candidate / 4;
            local front_index = candidate % 4;
            local dx = tile_index % search.side - search.radius;
            local dy = tile_index / search.side - search.radius;
            local tile = GSMap.GetTileIndex(search.center_x + dx, search.center_y + dy);
            if (!GSMap.IsValidTile(tile)) continue;
            local front = this.FrontTileForIndex(tile, front_index);
            if (!GSMap.IsValidTile(front)) continue;

            if (search.kind == "station") {
                if (GSRoad.IsRoadTile(tile)) {
                    local drive_through_estimate = this.EstimateStation(tile, front, true);
                    if (drive_through_estimate.success) {
                        this.CompletePlacementSearch(project, { tile = tile, front = front, drive_through = true });
                        return;
                    }

                    search.last_error = drive_through_estimate.error;
                }

                local station_estimate = this.EstimateStation(tile, front, false);
                if (station_estimate.success) {
                    this.CompletePlacementSearch(project, { tile = tile, front = front, drive_through = false });
                    return;
                }

                search.last_error = station_estimate.error;
            } else {
                local depot_estimate = this.EstimateDepot(tile, front);
                if (depot_estimate.success) {
                    this.CompletePlacementSearch(project, { tile = tile, front = front });
                    return;
                }

                search.last_error = depot_estimate.error;
            }
        }
    }

    function CompletePlacementSearch(project, plan) {
        local stage = project.placement_search.stage;
        project.rawdelete("placement_search");
        if (stage == "source") {
            project.source_station_tile <- plan.tile;
            project.source_station_front <- plan.front;
            project.source_station_drive_through <- plan.drive_through;
            this.BeginStationPlacementSearch(project, "destination", project.destination_town_id);
            return;
        }

        if (stage == "destination") {
            project.destination_station_tile <- plan.tile;
            project.destination_station_front <- plan.front;
            project.destination_station_drive_through <- plan.drive_through;
            this.BeginDepotPlacementSearch(project);
            return;
        }

        project.depot_tile <- plan.tile;
        project.depot_front <- plan.front;
        /* Build the approved stops and depot before surveying the final road
         * connection. Normal road stops and depots alter the adjacent native
         * road topology, so a route preflight against an empty map can become
        * invalid after those assets exist. */
        project.infrastructure_phase <- 0;
        project.state = "building_infrastructure";
        this.RecordEvent("ARENA-PROJECT-INFRASTRUCTURE", [project.project_id], "The bounded station and depot survey completed and persisted infrastructure construction began.", project.correlation_id);
    }

    function BeginRoadPathSearch(project, start_tile, target_tile, segment) {
        if (!GSMap.IsValidTile(start_tile) || !GSMap.IsValidTile(target_tile)) {
            this.BeginRecovery(project, "ARENA-ACTION-PATH-NOT-FOUND", "The constructed route assets do not expose two valid road-path endpoints.");
            return;
        }

        foreach (field in ["search_start", "search_target", "path_segment", "search_open", "search_best", "search_parent", "search_closed", "search_nodes"]) {
            if (project.rawin(field)) project.rawdelete(field);
        }
        project.search_start <- start_tile;
        project.search_target <- target_tile;
        project.path_segment <- segment;
        project.search_open <- [{ tile = start_tile, g = 0, f = this.PathHeuristic(start_tile, target_tile) }];
        project.search_best <- {};
        project.search_best[start_tile] <- 0;
        project.search_parent <- {};
        project.search_closed <- {};
        project.search_nodes <- 0;
    }

    function BeginRoadReplan(project, from_tile, native_error) {
        if (!project.rawin("path") || !project.rawin("source_path_index") ||
            !project.rawin("build_cursor") || !GSMap.IsValidTile(from_tile)) {
            this.BeginRecovery(project, "ARENA-ACTION-PATH-NOT-FOUND", "The persisted route could not safely recover its road-path state.");
            return false;
        }

        if (!project.rawin("replan_count")) project.replan_count <- 0;
        if (project.replan_count >= ArenaGS.MAX_PATH_REPLANS) {
            this.BeginRecovery(project, "ARENA-ACTION-PATH-NOT-FOUND", "The native road command remained unavailable after the bounded recovery limit (last native error=" + this.LimitText(native_error, 160, "unknown") + ").");
            return false;
        }

        local target = project.build_cursor < project.source_path_index
            ? project.source_station_front
            : project.destination_station_front;
        project.replan_count += 1;
        this.BeginRoadPathSearch(project, from_tile, target, 2);
        if (project.state == "recovering") return false;
        project.state = "surveying";
        this.RecordEvent("ARENA-ROUTE-PROGRESS", [project.project_id], "A changed road segment triggered bounded deterministic path recovery from the last verified connection.", project.correlation_id);
        return true;
    }

    function FrontTileForIndex(tile, index) {
        local x = GSMap.GetTileX(tile);
        local y = GSMap.GetTileY(tile);
        switch (index) {
            case 0: return GSMap.GetTileIndex(x + 1, y);
            case 1: return GSMap.GetTileIndex(x - 1, y);
            case 2: return GSMap.GetTileIndex(x, y + 1);
        }

        return GSMap.GetTileIndex(x, y - 1);
    }

    function NeighbourTiles(tile) {
        local result = [];
        if (!GSMap.IsValidTile(tile)) return result;
        local x = GSMap.GetTileX(tile);
        local y = GSMap.GetTileY(tile);
        local candidates = [
            GSMap.GetTileIndex(x + 1, y),
            GSMap.GetTileIndex(x - 1, y),
            GSMap.GetTileIndex(x, y + 1),
            GSMap.GetTileIndex(x, y - 1),
        ];
        foreach (candidate in candidates) if (GSMap.IsValidTile(candidate)) result.append(candidate);
        return result;
    }

    function PathHeuristic(from, to) {
        return GSMap.IsValidTile(from) && GSMap.IsValidTile(to) ? GSMap.DistanceManhattan(from, to) : ArenaGS.MAX_PATH_TILES;
    }

    function AdvanceProjectSurvey(project) {
        if (project.rawin("placement_search")) {
            this.AdvancePlacementSearch(project);
            return;
        }

        if (!project.rawin("search_start") || !project.rawin("search_target") || !project.rawin("path_segment")) {
            this.BeginRecovery(project, "ARENA-ACTION-PATH-NOT-FOUND", "The persisted road-path survey did not retain its bounded endpoints.");
            return;
        }

        for (local step = 0; step < ArenaGS.MAX_SEARCH_STEPS_PER_TICK; step++) {
            if (project.search_open.len() == 0) {
                this.BeginRecovery(project, "ARENA-ACTION-PATH-NOT-FOUND", "The bounded road-path search exhausted its deterministic frontier.");
                return;
            }

            if (project.search_nodes >= ArenaGS.MAX_SEARCH_NODES) {
                this.BeginRecovery(project, "ARENA-ACTION-PATH-NOT-FOUND", "The road-path search reached its certified node limit before finding a safe path.");
                return;
            }

            local node = this.PopLowestSearchNode(project.search_open);
            if (project.search_closed.rawin(node.tile)) continue;
            project.search_closed[node.tile] <- true;
            project.search_nodes += 1;
            if (node.tile == project.search_target) {
                local path = this.ReconstructPath(project, node.tile);
                if (path == null) {
                    this.BeginRecovery(project, "ARENA-ACTION-PATH-NOT-FOUND", "The road-path search returned an invalid persisted parent chain.");
                    return;
                }

                if (project.path_segment == 0) {
                    project.path <- path;
                    this.BeginRoadPathSearch(project, project.source_station_front, project.destination_station_front, 1);
                    return;
                }

                if (project.path_segment == 2) {
                    local recovered = [];
                    for (local prefix_index = 0; prefix_index < project.build_cursor; prefix_index++) recovered.append(project.path[prefix_index]);
                    foreach (path_tile in path) recovered.append(path_tile);

                    /* A recovery to the source access tile replaces only the
                     * depot-side suffix. Preserve the surveyed source-to-
                     * destination tail; otherwise a valid first-segment
                     * replan would silently turn the full route into a depot
                     * to source dead end. */
                    local recovered_source_index = null;
                    if (project.search_target == project.source_station_front) {
                        recovered_source_index = recovered.len() - 1;
                        for (local tail_index = project.source_path_index + 1; tail_index < project.path.len(); tail_index++) {
                            recovered.append(project.path[tail_index]);
                        }
                    }

                    if (recovered.len() > ArenaGS.MAX_PATH_TILES) {
                        this.BeginRecovery(project, "ARENA-ACTION-PATH-NOT-FOUND", "The recovered road path exceeds the certified maximum route length.");
                        return;
                    }

                    if (recovered_source_index != null) project.source_path_index = recovered_source_index;
                    project.path = recovered;
                    project.state = "building_infrastructure";
                    this.RecordEvent("ARENA-ROUTE-PROGRESS", [project.project_id], "The bounded road recovery found a replacement path from the last verified connection.", project.correlation_id);
                    return;
                }

                local combined = project.path;
                local source_path_index = combined.len() - 1;
                for (local path_index = 1; path_index < path.len(); path_index++) combined.append(path[path_index]);
                if (combined.len() > ArenaGS.MAX_PATH_TILES) {
                    this.BeginRecovery(project, "ARENA-ACTION-PATH-NOT-FOUND", "The connected depot and station path exceeds the certified maximum route length.");
                    return;
                }

                project.path <- combined;
                project.source_path_index <- source_path_index;
                project.replan_count <- 0;
                project.build_cursor <- 0;
                project.infrastructure_phase <- 3;
                project.state = "building_infrastructure";
                this.RecordEvent("ARENA-ROUTE-PROGRESS", [project.project_id], "The final road path was surveyed against the constructed stations and depot.", project.correlation_id);
                return;
            }

            foreach (next_tile in this.NeighbourTiles(node.tile)) {
                if (project.search_closed.rawin(next_tile) || !this.CanTraverseOrBuildRoad(node.tile, next_tile)) continue;
                local next_cost = node.g + 1;
                if (!project.search_best.rawin(next_tile) || next_cost < project.search_best[next_tile]) {
                    project.search_best[next_tile] <- next_cost;
                    project.search_parent[next_tile] <- node.tile;
                    project.search_open.append({
                        tile = next_tile,
                        g = next_cost,
                    f = next_cost + this.PathHeuristic(next_tile, project.search_target),
                    });
                }
            }
        }
    }

    function PopLowestSearchNode(nodes) {
        local best_index = 0;
        for (local index = 1; index < nodes.len(); index++) {
            local candidate = nodes[index];
            local current = nodes[best_index];
            if (candidate.f < current.f || (candidate.f == current.f && candidate.tile < current.tile)) best_index = index;
        }

        local result = nodes[best_index];
        nodes.remove(best_index);
        return result;
    }

    function ReconstructPath(project, destination) {
        local reverse = [];
        local current = destination;
        for (local guard = 0; guard < ArenaGS.MAX_PATH_TILES; guard++) {
            reverse.append(current);
            if (current == project.search_start) {
                local path = [];
                for (local index = reverse.len() - 1; index >= 0; index--) path.append(reverse[index]);
                return path;
            }

            if (!project.search_parent.rawin(current)) return null;
            current = project.search_parent[current];
        }

        return null;
    }

    function CanTraverseOrBuildRoad(from, to) {
        if (!GSMap.IsValidTile(from) || !GSMap.IsValidTile(to)) return false;
        if (GSRoad.IsRoadTile(from) && GSRoad.IsRoadTile(to) && GSRoad.AreRoadTilesConnected(from, to)) return true;
        return this.EstimateRoad(from, to).success;
    }

    function AdvanceInfrastructure(project) {
        if (project.infrastructure_phase == 0) {
            if (!this.BuildProjectStation(project, true)) return;
            project.infrastructure_phase = 1;
            return;
        }

        if (project.infrastructure_phase == 1) {
            if (!this.BuildProjectStation(project, false)) return;
            project.infrastructure_phase = 2;
            return;
        }

        if (project.infrastructure_phase == 2) {
            if (!this.BuildProjectDepot(project)) return;
            project.infrastructure_phase = 3;
            this.BeginRoadPathSearch(project, project.depot_front, project.source_station_front, 0);
            if (project.state == "recovering") return;
            project.state = "surveying";
            this.RecordEvent("ARENA-PROJECT-SURVEYING", [project.project_id], "The project began its final bounded road-path survey against the constructed depot and stations.", project.correlation_id);
            return;
        }

        if (project.build_cursor >= project.path.len() - 1) {
            project.state = "buying_vehicles";
            this.RecordEvent("ARENA-PROJECT-BUYING-VEHICLES", [project.project_id, project.route_id], "Road infrastructure is complete and the project began selecting compatible passenger vehicles.", project.correlation_id);
            return;
        }

        local from = project.path[project.build_cursor];
        local to = project.path[project.build_cursor + 1];
        if (!GSRoad.IsRoadTile(from) || !GSRoad.IsRoadTile(to) || !GSRoad.AreRoadTilesConnected(from, to)) {
            if (!this.ExecuteRoad(project, from, to)) return;
        }

        project.build_cursor += 1;
        if (project.build_cursor % 8 == 0 || project.build_cursor >= project.path.len() - 1) {
            this.RecordEvent("ARENA-ROUTE-PROGRESS", [project.project_id], "The deterministic road construction cursor advanced within the declared project budget.", project.correlation_id);
        }
    }

    function BuildProjectStation(project, source) {
        local tile = source ? project.source_station_tile : project.destination_station_tile;
        local front = source ? project.source_station_front : project.destination_station_front;
        local drive_through = source ? project.source_station_drive_through : project.destination_station_drive_through;
        local estimate = this.EstimateStation(tile, front, drive_through);
        if (!estimate.success) {
            this.BeginRecovery(project, "ARENA-ACTION-STATION-PLACEMENT-FAILED", "A previously validated station placement was no longer buildable.");
            return false;
        }

        if (!this.CanSpend(project, estimate.cost)) {
            this.BeginRecovery(project, "ARENA-ACTION-BUDGET-EXCEEDED", "The next station command would exceed the declared maximum budget or available company cash.");
            return false;
        }

        local accounting = GSAccounting();
        local built = drive_through
            ? GSRoad.BuildDriveThroughRoadStation(tile, front, GSRoad.ROADVEHTYPE_BUS, GSStation.STATION_NEW)
            : GSRoad.BuildRoadStation(tile, front, GSRoad.ROADVEHTYPE_BUS, GSStation.STATION_NEW);
        if (!built || !this.RecordActualSpend(project, accounting.GetCosts(), estimate.cost)) {
            if (built) this.BeginRecovery(project, "ARENA-ACTION-BUDGET-EXCEEDED", "The station command cost changed after preflight and the project stopped safely.");
            else this.BeginRecovery(project, "ARENA-ACTION-STATION-PLACEMENT-FAILED", "The native game rejected the station placement.");
            return false;
        }

        local station_id = GSStation.GetStationID(tile);
        if (!GSStation.IsValidStation(station_id)) {
            this.BeginRecovery(project, "ARENA-ACTION-STATION-PLACEMENT-FAILED", "The station command completed without a valid owned station identifier.");
            return false;
        }

        /* Use the native endpoint returned after construction, rather than
         * retaining the placement probe's proposed direction. This keeps the
         * persisted road search aligned with the actual station topology when
         * OpenTTD resolves a permitted stop orientation. */
        local actual_front = GSRoad.GetRoadStationFrontTile(tile);
        if (!GSMap.IsValidTile(actual_front)) {
            this.BeginRecovery(project, "ARENA-ACTION-STATION-PLACEMENT-FAILED", "The native station command completed without a usable road-access tile.");
            return false;
        }

        if (source) {
            project.source_station_id <- station_id;
            project.source_station_front <- actual_front;
        } else {
            project.destination_station_id <- station_id;
            project.destination_station_front <- actual_front;
        }
        this.RecordEvent("ARENA-STATION-CREATED", [project.project_id, "station-" + station_id], "A passenger station was created by the persisted route project.", project.correlation_id);
        return true;
    }

    function BuildProjectDepot(project) {
        local estimate = this.EstimateDepot(project.depot_tile, project.depot_front);
        if (!estimate.success) {
            this.BeginRecovery(project, "ARENA-ACTION-STATION-PLACEMENT-FAILED", "A previously validated road depot placement was no longer buildable.");
            return false;
        }

        if (!this.CanSpend(project, estimate.cost)) {
            this.BeginRecovery(project, "ARENA-ACTION-BUDGET-EXCEEDED", "The next depot command would exceed the declared maximum budget or available company cash.");
            return false;
        }

        local accounting = GSAccounting();
        if (!GSRoad.BuildRoadDepot(project.depot_tile, project.depot_front) || !this.RecordActualSpend(project, accounting.GetCosts(), estimate.cost)) {
            this.BeginRecovery(project, "ARENA-ACTION-STATION-PLACEMENT-FAILED", "The native game rejected the road depot placement.");
            return false;
        }

        local actual_front = GSRoad.GetRoadDepotFrontTile(project.depot_tile);
        if (!GSMap.IsValidTile(actual_front)) {
            this.BeginRecovery(project, "ARENA-ACTION-STATION-PLACEMENT-FAILED", "The native depot command completed without a usable road-access tile.");
            return false;
        }

        project.depot_front <- actual_front;
        this.RecordEvent("ARENA-DEPOT-CREATED", [project.project_id], "A road depot was created for the persisted route project.", project.correlation_id);
        return true;
    }

    function EstimateRoad(from, to) {
        local test = GSTestMode();
        local accounting = GSAccounting();
        local success = GSRoad.BuildRoad(from, to);
        local result = { success = success, cost = this.AbsoluteCost(accounting.GetCosts()) };
        /* GSTestMode restores the native command mode when the instance is
         * destroyed. Explicitly release it before returning from high-volume
         * survey probes so test-mode scopes never accumulate across ticks. */
        test = null;
        return result;
    }

    function EstimateStation(tile, front, drive_through) {
        local test = GSTestMode();
        local accounting = GSAccounting();
        local success = drive_through
            ? GSRoad.BuildDriveThroughRoadStation(tile, front, GSRoad.ROADVEHTYPE_BUS, GSStation.STATION_NEW)
            : GSRoad.BuildRoadStation(tile, front, GSRoad.ROADVEHTYPE_BUS, GSStation.STATION_NEW);
        local result = { success = success, cost = this.AbsoluteCost(accounting.GetCosts()), error = success ? "none" : GSError.GetLastErrorString() };
        test = null;
        return result;
    }

    function EstimateDepot(tile, front) {
        local test = GSTestMode();
        local accounting = GSAccounting();
        local success = GSRoad.BuildRoadDepot(tile, front);
        local result = { success = success, cost = this.AbsoluteCost(accounting.GetCosts()) };
        test = null;
        return result;
    }

    function ExecuteRoad(project, from, to) {
        local estimate = this.EstimateRoad(from, to);
        if (!estimate.success) {
            this.BeginRoadReplan(project, from, GSError.GetLastErrorString());
            return false;
        }

        if (!this.CanSpend(project, estimate.cost)) {
            this.BeginRecovery(project, "ARENA-ACTION-BUDGET-EXCEEDED", "The next road segment would exceed the declared maximum budget or available company cash.");
            return false;
        }

        local accounting = GSAccounting();
        if (!GSRoad.BuildRoad(from, to)) {
            this.BeginRoadReplan(project, from, GSError.GetLastErrorString());
            return false;
        }

        if (!this.RecordActualSpend(project, accounting.GetCosts(), estimate.cost)) {
            this.BeginRecovery(project, "ARENA-ACTION-BUDGET-EXCEEDED", "The native road command cost changed after preflight and the project stopped before exceeding its declared budget.");
            return false;
        }

        return true;
    }

    function CanSpend(project, expected_cost) {
        return typeof expected_cost == "integer" && expected_cost >= 0 &&
            project.spent + expected_cost <= project.maximum_budget &&
            GSCompany.GetBankBalance(project.company_id) >= expected_cost;
    }

    function RecordActualSpend(project, raw_cost, expected_cost) {
        local actual_cost = this.AbsoluteCost(raw_cost);
        if (actual_cost > expected_cost || project.spent + actual_cost > project.maximum_budget) return false;
        project.spent += actual_cost;
        return true;
    }

    function AbsoluteCost(value) {
        if (typeof value != "integer") return 0;
        return value < 0 ? -value : value;
    }

    function AdvanceVehiclePurchase(project) {
        if (!project.rawin("engine_id")) {
            local engine_id = this.FindPassengerRoadEngine(project.cargo_id);
            if (engine_id == null) {
                this.BeginRecovery(project, "ARENA-ACTION-VEHICLE-UNSUITABLE", "No buildable road vehicle can carry the selected passenger cargo on the current road type.");
                return;
            }

            project.engine_id <- engine_id;
        }

        if (project.vehicle_ids.len() >= project.initial_vehicle_count) {
            local topology_error = this.OperationalRouteTopologyError(project);
            if (topology_error != null) {
                this.BeginRecovery(project, "ARENA-ACTION-PATH-NOT-FOUND", topology_error);
                return;
            }

            project.order_cursor <- 0;
            project.state = "configuring_orders";
            this.RecordEvent("ARENA-PROJECT-CONFIGURING-ORDERS", [project.project_id, project.route_id], "The requested passenger fleet was purchased and route orders are being configured.", project.correlation_id);
            return;
        }

        local estimate = this.EstimateVehicle(project.depot_tile, project.engine_id, project.cargo_id);
        if (!estimate.success) {
            this.BeginRecovery(project, "ARENA-ACTION-VEHICLE-UNSUITABLE", "The selected passenger road vehicle is no longer buildable at the project depot.");
            return;
        }

        if (!this.CanSpend(project, estimate.cost)) {
            this.BeginRecovery(project, "ARENA-ACTION-BUDGET-EXCEEDED", "The next passenger vehicle would exceed the declared maximum budget or available company cash.");
            return;
        }

        local accounting = GSAccounting();
        local vehicle_id = GSVehicle.BuildVehicleWithRefit(project.depot_tile, project.engine_id, project.cargo_id);
        if (!GSVehicle.IsValidVehicle(vehicle_id) || !this.RecordActualSpend(project, accounting.GetCosts(), estimate.cost)) {
            this.BeginRecovery(project, "ARENA-ACTION-VEHICLE-UNSUITABLE", "The native game rejected a passenger vehicle purchase.");
            return;
        }

        project.vehicle_ids.append(vehicle_id);
        this.RecordEvent("ARENA-VEHICLE-CREATED", [project.project_id, "vehicle-" + vehicle_id], "A compatible passenger road vehicle was purchased within the declared project budget.", project.correlation_id);
    }

    function FindPassengerRoadEngine(cargo_id) {
        local engines = GSEngineList(GSVehicle.VT_ROAD);
        engines.Sort(GSList.SORT_BY_ITEM, GSList.SORT_ASCENDING);
        local selected = null;
        local selected_price = null;
        for (local engine = engines.Begin(); !engines.IsEnd(); engine = engines.Next()) {
            if (!GSEngine.IsValidEngine(engine) || !GSEngine.IsBuildable(engine) ||
                !GSEngine.CanRunOnRoad(engine, GSRoad.ROADTYPE_ROAD) ||
                !GSEngine.CanRefitCargo(engine, cargo_id)) continue;
            local price = GSEngine.GetPrice(engine);
            if (selected == null || price < selected_price || (price == selected_price && engine < selected)) {
                selected = engine;
                selected_price = price;
            }
        }

        return selected;
    }

    function EstimateVehicle(depot_tile, engine_id, cargo_id) {
        local test = GSTestMode();
        local accounting = GSAccounting();
        local vehicle_id = GSVehicle.BuildVehicleWithRefit(depot_tile, engine_id, cargo_id);
        local result = { success = vehicle_id == 0, cost = this.AbsoluteCost(accounting.GetCosts()) };
        test = null;
        return result;
    }

    function AdvanceOrderConfiguration(project) {
        if (project.order_cursor >= project.vehicle_ids.len()) {
            project.verification_started_tick <- this._tick;
            project.verification_locations <- {};
            foreach (vehicle_id in project.vehicle_ids) {
                if (GSVehicle.IsValidVehicle(vehicle_id)) project.verification_locations[vehicle_id] <- GSVehicle.GetLocation(vehicle_id);
            }

            project.state = "verifying";
            this.RecordEvent("ARENA-PROJECT-VERIFYING", [project.project_id, project.route_id], "The passenger fleet has valid route orders and the project is verifying real traversal.", project.correlation_id);
            return;
        }

        local vehicle_id = project.vehicle_ids[project.order_cursor];
        if (!GSVehicle.IsValidVehicle(vehicle_id) || !GSVehicle.IsPrimaryVehicle(vehicle_id)) {
            this.BeginRecovery(project, "ARENA-ACTION-ORDER-INVALID", "A purchased vehicle is no longer valid while route orders are being configured.");
            return;
        }

        local order_count = GSOrder.GetOrderCount(vehicle_id);
        if (order_count == 0) {
            if (!GSOrder.AppendOrder(vehicle_id, project.source_station_tile, GSOrder.OF_NONE) ||
                !GSOrder.AppendOrder(vehicle_id, project.destination_station_tile, GSOrder.OF_NONE)) {
                this.BeginRecovery(project, "ARENA-ACTION-ORDER-INVALID", "The native game rejected the two required station orders for a passenger vehicle.");
                return;
            }

            order_count = GSOrder.GetOrderCount(vehicle_id);
        }

        if (order_count < 2) {
            this.BeginRecovery(project, "ARENA-ACTION-ORDER-INVALID", "The passenger vehicle does not have the two required station orders.");
            return;
        }

        local state = GSVehicle.GetState(vehicle_id);
        if (state == GSVehicle.VS_STOPPED || state == GSVehicle.VS_IN_DEPOT) {
            if (!GSVehicle.StartStopVehicle(vehicle_id)) {
                this.BeginRecovery(project, "ARENA-ACTION-ORDER-INVALID", "The native game could not start a passenger vehicle after assigning route orders.");
                return;
            }
        }

        project.order_cursor += 1;
    }

    function OperationalRouteTopologyError(project) {
        if (!project.rawin("path") || project.path.len() < 2) return "The persisted project no longer contains a complete road path.";
        if (!project.rawin("depot_tile") || !GSRoad.IsRoadDepotTile(project.depot_tile)) return "The persisted project depot is no longer a native road depot.";
        if (!project.rawin("source_station_tile") || !GSRoad.IsRoadStationTile(project.source_station_tile)) return "The persisted source stop is no longer a native road station.";
        if (!project.rawin("destination_station_tile") || !GSRoad.IsRoadStationTile(project.destination_station_tile)) return "The persisted destination stop is no longer a native road station.";
        if (GSRoad.GetRoadDepotFrontTile(project.depot_tile) != project.depot_front) return "The persisted depot access tile no longer matches the native depot front.";
        if (GSRoad.GetRoadStationFrontTile(project.source_station_tile) != project.source_station_front) return "The persisted source station access tile no longer matches the native station front.";
        if (GSRoad.GetRoadStationFrontTile(project.destination_station_tile) != project.destination_station_front) return "The persisted destination station access tile no longer matches the native station front.";

        for (local index = 0; index < project.path.len(); index++) {
            if (!GSRoad.IsRoadTile(project.path[index])) return "The persisted road path contains a tile that is not traversable road.";
            if (index > 0 && !GSRoad.AreRoadTilesConnected(project.path[index - 1], project.path[index])) return "The persisted road path contains a disconnected native road segment.";
        }

        if (project.path[0] != project.depot_front) return "The persisted road path no longer starts at the native depot access tile.";
        if (project.path[project.path.len() - 1] != project.destination_station_front) return "The persisted road path no longer ends at the native destination station access tile.";
        if (!project.rawin("source_path_index") || project.source_path_index <= 0 || project.source_path_index >= project.path.len() - 1 || project.path[project.source_path_index] != project.source_station_front) return "The persisted road path no longer contains the native source station access tile in route order.";

        return null;
    }

    function AdvanceVerification(project) {
        if (!GSStation.IsValidStation(project.source_station_id) || !GSStation.IsValidStation(project.destination_station_id) ||
            project.vehicle_ids.len() < 1 || project.vehicle_ids.len() != project.initial_vehicle_count) {
            this.BeginRecovery(project, "ARENA-ACTION-ORDER-INVALID", "The route lost a required station or vehicle before operational verification completed.");
            return;
        }

        local moved = false;
        local traversing = false;
        local all_started = true;
        foreach (vehicle_id in project.vehicle_ids) {
            if (!GSVehicle.IsValidVehicle(vehicle_id) || GSOrder.GetOrderCount(vehicle_id) < 2) {
                this.BeginRecovery(project, "ARENA-ACTION-ORDER-INVALID", "A route vehicle no longer has the required valid order set.");
                return;
            }

            local state = GSVehicle.GetState(vehicle_id);
            if (state == GSVehicle.VS_STOPPED || state == GSVehicle.VS_IN_DEPOT) {
                all_started = false;
                /* A state change can race the command boundary by one game
                 * tick. Retry the bounded native start command only while
                 * the vehicle remains stopped at its depot. */
                if ((this._tick - project.verification_started_tick) % 74 == 0 && !GSVehicle.StartStopVehicle(vehicle_id)) {
                    this.BeginRecovery(project, "ARENA-ACTION-ORDER-INVALID", "A configured passenger vehicle could not be started for route verification.");
                    return;
                }
            }

            local location = GSVehicle.GetLocation(vehicle_id);
            if (project.verification_locations.rawin(vehicle_id) && location != project.verification_locations[vehicle_id]) moved = true;
            if (state == GSVehicle.VS_RUNNING && !GSVehicle.IsInDepot(vehicle_id) && GSVehicle.GetCurrentSpeed(vehicle_id) > 0) traversing = true;
        }

        if (moved || traversing) {
            project.state = "completed";
            this.RecordEvent("ARENA-ROUTE-OPERATING", [project.project_id, project.route_id], "The passenger route has valid stations, depot access, vehicles, orders, and demonstrated non-depot road movement.", project.correlation_id);
            return;
        }

        local verification_ticks = this._tick - project.verification_started_tick;
        if (!all_started && verification_ticks > ArenaGS.MAX_VEHICLE_START_TICKS) {
            this.BeginRecovery(project, "ARENA-ACTION-ORDER-INVALID", "A configured passenger vehicle remained stopped in its depot after the bounded start window. " + this.VerificationVehicleDiagnostics(project));
            return;
        }

        if (verification_ticks > ArenaGS.MAX_ROUTE_STALL_TICKS) {
            this.BeginRecovery(project, "ARENA-ACTION-PATH-NOT-FOUND", "A running passenger vehicle did not leave its initial route tile during the bounded traversal window. " + this.VerificationVehicleDiagnostics(project));
            return;
        }

        if (verification_ticks > ArenaGS.MAX_VERIFICATION_TICKS) {
            this.BeginRecovery(project, "ARENA-ACTION-VERIFICATION-TIMED-OUT", "The passenger route did not demonstrate vehicle movement before the bounded verification timeout. " + this.VerificationVehicleDiagnostics(project));
        }
    }

    function VerificationVehicleDiagnostics(project) {
        local details = [];
        foreach (vehicle_id in project.vehicle_ids) {
            if (!GSVehicle.IsValidVehicle(vehicle_id)) {
                details.append("vehicle-" + vehicle_id + " is invalid");
                continue;
            }

            local location = GSVehicle.GetLocation(vehicle_id);
            local coordinate = this.CoordinatePayload(location);
            details.append("vehicle-" + vehicle_id + "=" + this.VehicleStateText(GSVehicle.GetState(vehicle_id)) +
                " at " + coordinate.x + "," + coordinate.y + " speed=" + GSVehicle.GetCurrentSpeed(vehicle_id));
            if (details.len() >= 4) break;
        }

        if (details.len() == 0) return "No valid route-vehicle diagnostic was available.";
        local summary = "Vehicle state: ";
        for (local index = 0; index < details.len(); index++) {
            if (index > 0) summary += "; ";
            summary += details[index];
        }

        return summary + ".";
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
        this.SendChunkedPayload(request, "snapshot_result", payload_json);
    }

    function SendChunkedSnapshot(request, payload) {
        this.SendChunkedPayload(request, "snapshot_result", this.JsonEncode(payload));
    }

    function SendChunkedPayload(request, logical_message_type, payload_json) {
        if (payload_json.len() < 1 || payload_json.len() > ArenaGS.MAX_LOGICAL_BYTES) {
            this.SendError(request, "ARENA-PROTOCOL-MESSAGE-TOO-LARGE", "The bounded ArenaGS response exceeds the logical protocol limit.");
            return;
        }

        local encoded = this.Base64Encode(payload_json);
        local total_chunks = ((encoded.len() + ArenaGS.MAX_CHUNK_DATA - 1) / ArenaGS.MAX_CHUNK_DATA).tointeger();
        if (total_chunks > ArenaGS.MAX_CHUNKS) {
            this.SendError(request, "ARENA-PROTOCOL-MESSAGE-TOO-LARGE", "The bounded ArenaGS response exceeds the protocol chunk count.");
            return;
        }

        if (this._outbound_transfers.len() >= ArenaGS.MAX_TRANSFERS) {
            this.SendError(request, "ARENA-PROTOCOL-CHUNK-INVALID", "Too many bounded ArenaGS response transfers are active.");
            return;
        }

        /* GSAdmin.Send has a small per-tick queue. Draining one envelope per
         * GameScript slice avoids dropping the third and later chunks of a
         * normal rich snapshot while preserving the existing checksum and
         * reassembly contract. */
        this._message_sequence += 1;
        this._outbound_transfers.append({
            run_id = request.run_id,
            request_message_id = request.message_id,
            correlation_id = request.correlation_id,
            idempotency_key = request.idempotency_key,
            logical_message_type = logical_message_type,
            logical_message_id = "arena-" + this._message_sequence,
            transfer_id = "out-" + this._message_sequence,
            total_chunks = total_chunks,
            logical_bytes = payload_json.len(),
            checksum = this.Adler32(encoded),
            encoded = encoded,
            sequence = 0,
        });
    }

    function DrainOutboundTransfers() {
        if (this._outbound_transfers == null || this._outbound_transfers.len() == 0) return;
        local transfer = this._outbound_transfers[0];
        local start = transfer.sequence * ArenaGS.MAX_CHUNK_DATA;
        local finish = start + ArenaGS.MAX_CHUNK_DATA;
        if (finish > transfer.encoded.len()) finish = transfer.encoded.len();
        if (!this.SendAdminResponse({
            protocol_version = ArenaGS.PROTOCOL_VERSION,
            message_type = "chunk",
            run_id = transfer.run_id,
            message_id = "chunk-" + transfer.logical_message_id + "-" + transfer.sequence,
            correlation_id = transfer.correlation_id,
            idempotency_key = transfer.idempotency_key,
            payload = {
                transfer_id = transfer.transfer_id,
                logical_message_type = transfer.logical_message_type,
                logical_message_id = transfer.logical_message_id,
                logical_correlation_id = transfer.correlation_id,
                logical_idempotency_key = transfer.idempotency_key,
                sequence = transfer.sequence,
                total_chunks = transfer.total_chunks,
                logical_bytes = transfer.logical_bytes,
                encoding = "base64_utf8",
                checksum = transfer.checksum,
                data = transfer.encoded.slice(start, finish),
            },
        })) {
            this._outbound_transfers.remove(0);
            this.SendError({
                run_id = transfer.run_id,
                message_id = transfer.request_message_id,
                correlation_id = transfer.correlation_id,
                idempotency_key = transfer.idempotency_key,
                message_type = "snapshot_request",
            }, "ARENA-PROTOCOL-MESSAGE-TOO-LARGE", "ArenaGS could not send a bounded response chunk.");
            return;
        }

        transfer.sequence += 1;
        if (transfer.sequence >= transfer.total_chunks) this._outbound_transfers.remove(0);
    }

    /* ArenaGS response data is restricted to the JSON values below. This
     * local encoder is used only to feed the existing checksum/chunk envelope;
     * it keeps large authoritative snapshots within the same bounded protocol
     * shape the .NET bridge already verifies. */
    function JsonEncode(value) {
        if (value == null) return "null";
        local kind = typeof value;
        if (kind == "bool") return value ? "true" : "false";
        if (kind == "integer") return value.tostring();
        if (kind == "string") return "\"" + this.JsonEscape(value) + "\"";
        if (kind == "array") {
            local result = "[";
            for (local index = 0; index < value.len(); index++) {
                if (index > 0) result += ",";
                result += this.JsonEncode(value[index]);
            }

            return result + "]";
        }

        if (kind == "table") {
            local result = "{";
            local first = true;
            foreach (key, entry in value) {
                if (typeof key != "string") throw "ArenaGS response table keys must be strings.";
                if (!first) result += ",";
                result += "\"" + this.JsonEscape(key) + "\":" + this.JsonEncode(entry);
                first = false;
            }

            return result + "}";
        }

        throw "ArenaGS response contains an unsupported JSON value.";
    }

    function JsonEscape(value) {
        local result = "";
        for (local index = 0; index < value.len(); index++) {
            local character = value[index];
            if (character == 34) result += "\\\"";
            else if (character == 92) result += "\\\\";
            else if (character == 8) result += "\\b";
            else if (character == 9) result += "\\t";
            else if (character == 10) result += "\\n";
            else if (character == 12) result += "\\f";
            else if (character == 13) result += "\\r";
            else if (character < 32) result += "\\u00" + this.Hex2(character);
            else result += value.slice(index, index + 1);
        }

        return result;
    }

    function Hex2(value) {
        local characters = "0123456789abcdef";
        local high = ((value / 16).tointeger()) % 16;
        local low = value % 16;
        return characters.slice(high, high + 1) + characters.slice(low, low + 1);
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
