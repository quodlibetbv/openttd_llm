class ArenaGSInfo extends GSInfo {
    function GetAuthor()      { return "OpenTTD Model Arena"; }
    function GetName()        { return "ArenaGS"; }
    function GetShortName()   { return "ARGS"; }
    function GetDescription() { return "Authoritative Arena GameScript and AdminPort protocol boundary."; }
    function GetVersion()     { return 2; }
    function GetDate()        { return "2026-07-24"; }
    function CreateInstance() { return "ArenaGS"; }
    function GetAPIVersion()  { return "1.2"; }
}

RegisterGS(ArenaGSInfo());
