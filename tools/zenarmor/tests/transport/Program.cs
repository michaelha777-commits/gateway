using System.Diagnostics;
// Test-only process adapter. Production invokes OpenSSH.
var start = new ProcessStartInfo(Environment.GetEnvironmentVariable("TEST_PYTHON")!) { UseShellExecute = false };
start.ArgumentList.Add(Environment.GetEnvironmentVariable("TEST_READER")!);
using var child = Process.Start(start)!;
child.WaitForExit();
return child.ExitCode;
