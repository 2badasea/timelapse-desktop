using System.Diagnostics;
using System.Text;

namespace TimelapseConverter.Services;

public sealed record ProcessResult(int ExitCode, string Stdout, string ErrorTail);

public static class ProcessRunner
{
    const int ErrorTailLines = 30;

    /// <summary>
    /// 콘솔 창 없이 프로세스를 실행한다. 취소되면 프로세스를 강제 종료한다.
    /// onStdoutLine을 주면 표준출력을 줄 단위로 넘기고, 아니면 모아서 반환한다.
    /// </summary>
    public static async Task<ProcessResult> RunAsync(
        string exe, IEnumerable<string> args, CancellationToken ct, Action<string>? onStdoutLine = null)
    {
        var psi = new ProcessStartInfo(exe)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);

        using var process = new Process { StartInfo = psi };
        var stdout = new StringBuilder();
        var errTail = new Queue<string>();

        process.OutputDataReceived += (_, e) =>
        {
            if (e.Data == null) return;
            if (onStdoutLine != null) onStdoutLine(e.Data);
            else lock (stdout) stdout.AppendLine(e.Data);
        };
        process.ErrorDataReceived += (_, e) =>
        {
            if (e.Data == null) return;
            lock (errTail)
            {
                errTail.Enqueue(e.Data);
                while (errTail.Count > ErrorTailLines) errTail.Dequeue();
            }
        };

        ct.ThrowIfCancellationRequested();
        process.Start();
        ChildProcessGuard.Attach(process);
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        using (ct.Register(() => { try { process.Kill(entireProcessTree: true); } catch { } }))
        {
            await process.WaitForExitAsync().ConfigureAwait(false);
        }
        process.WaitForExit(); // 비동기 출력 이벤트가 끝까지 처리되도록 보장

        string err;
        lock (errTail) err = string.Join(Environment.NewLine, errTail);
        return new ProcessResult(process.ExitCode, stdout.ToString(), err);
    }
}
