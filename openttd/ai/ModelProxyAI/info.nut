class ModelProxyAIInfo extends AIInfo {
    function GetAuthor()      { return "OpenTTD Model Arena"; }
    function GetName()        { return "ModelProxyAI"; }
    function GetDescription() { return "Inert benchmark-company ownership proxy."; }
    function GetVersion()     { return 1; }
    function GetDate()        { return "2026-07-23"; }
    function CreateInstance() { return "ModelProxyAI"; }
    function GetAPIVersion()  { return "1.0"; }
}

RegisterAI(ModelProxyAIInfo());
