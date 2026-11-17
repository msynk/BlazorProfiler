using System.Text;
using BlazorProfiler.Cli;

using var cancellation = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) =>
{
    e.Cancel = true;
    cancellation.Cancel();
};

if (args.Length > 0 && args[0] == "mcp")
{
    // The MCP stdio transport is UTF-8 JSON-RPC; nothing else may be written to standard output.
    Console.InputEncoding = new UTF8Encoding(false);
    Console.OutputEncoding = new UTF8Encoding(false);
}

return await TraceCli.RunAsync(args, Console.In, Console.Out, Console.Error, cancellation.Token);
