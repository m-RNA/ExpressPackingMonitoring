using ExpressPackingMonitoring.Data;
using ExpressPackingMonitoring.Services;

if (args.Length < 3 || !int.TryParse(args[0], out int port))
{
    Console.Error.WriteLine("Usage: <port> <data-dir> <fixture-video>");
    return 2;
}

string dataDir = Path.GetFullPath(args[1]);
string fixtureVideo = Path.GetFullPath(args[2]);
Directory.CreateDirectory(dataDir);
Environment.SetEnvironmentVariable("EPM_USER_DATA_DIR", dataDir);

using var database = new VideoDatabase(Path.Combine(dataDir, "automation.db"));
long recordId = database.InsertVideoRecord(
    "AUTO_WEB_001",
    "发货",
    "H264",
    "automation",
    fixtureVideo,
    DateTime.Now.AddSeconds(-2));
database.UpdateVideoRecordOnStop(
    recordId,
    DateTime.Now,
    2,
    new FileInfo(fixtureVideo).Length,
    "automation",
    "H264",
    "automation");

using var server = new WebServer(
    database,
    port,
    transCacheMaxMB: 64,
    listenerHost: "127.0.0.1",
    mobileConnectionUrlProvider: () => $"http://192.168.1.20:{port}");
server.Start();
Console.WriteLine($"READY http://127.0.0.1:{port}/");
Console.Out.Flush();

var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
Console.CancelKeyPress += (_, eventArgs) =>
{
    eventArgs.Cancel = true;
    completion.TrySetResult();
};
AppDomain.CurrentDomain.ProcessExit += (_, _) => completion.TrySetResult();
await completion.Task;
return 0;
