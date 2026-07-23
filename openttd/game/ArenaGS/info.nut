class ArenaGSInfo extends GSInfo {
    function GetAuthor()      { return "OpenTTD Model Arena"; }
    function GetName()        { return "ArenaGS"; }
    function GetDescription() { return "Authoritative Arena GameScript foundation package."; }
    function GetVersion()     { return 1; }
    function GetDate()        { return "2026-07-23"; }
    function CreateInstance() { return "ArenaGS"; }
    function GetAPIVersion()  { return "1.0"; }
}

RegisterGS(ArenaGSInfo());
