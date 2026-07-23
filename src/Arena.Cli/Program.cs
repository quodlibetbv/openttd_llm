using OpenTtd.ModelArena.Contracts;

if (args.Length == 1 && string.Equals(args[0], "--version", StringComparison.Ordinal))
{
    Console.WriteLine("ttd-arena foundation 0.1.0");
    return;
}

Console.WriteLine("OpenTTD Model Arena foundation baseline");
Console.WriteLine($"Protocol contract: {ContractVersions.ProtocolV1}");
Console.WriteLine("Phase 00 provides contracts and validation only; gameplay commands begin in later phases.");
