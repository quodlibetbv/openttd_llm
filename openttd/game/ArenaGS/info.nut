class ArenaGSInfo extends GSInfo {
    function GetAuthor()      { return "OpenTTD Model Arena"; }
    function GetName()        { return "ArenaGS"; }
    function GetShortName()   { return "ARGS"; }
    function GetDescription() { return "Authoritative Arena GameScript and AdminPort protocol boundary."; }
    function GetVersion()     { return 3; }
    /* Version 3 adds persisted Phase 04/06 state but can restore the Phase 03
     * bridge save format because Load() treats the new fields as optional. */
    function MinVersionToLoad() { return 2; }
    function GetDate()        { return "2026-07-24"; }
    function CreateInstance() { return "ArenaGS"; }
    /* ArenaGS deliberately targets the certified OpenTTD 14 API. Phase 04
     * snapshots and Phase 06 route execution use the current typed station,
     * vehicle, and road surfaces; loading them through the legacy 1.2
     * compatibility layer changes method signatures at runtime. */
    function GetAPIVersion()  { return "14"; }
}

RegisterGS(ArenaGSInfo());
