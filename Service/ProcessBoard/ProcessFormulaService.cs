using System.Diagnostics;
using System.Reflection;
using Interface;
using Microsoft.Extensions.Options;
using Model.ProcessBoard;
using Newtonsoft.Json;
using SqlSugar;

namespace Service;

/// <summary>复杂公式不占用 ASP.NET 请求执行线程，超时可终止进程而非仅停止等待。</summary>
public sealed class ProcessFormulaService(IOptions<ProcessFormulaOptions> options, SqlSugarScope? scope = null) : IProcessFormulaService
{
    private static readonly SemaphoreSlim Workers = new(2, 2);

    public async Task<ProcessFormulaResult> EvaluateAsync(ProcessFormulaRequest request,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(request.Formula)) return new(false, null, "公式不能为空");
        if (request.Variables == null || request.Constants == null || request.Functions == null || request.Functions.Any(x => x == null))
            return new(false, null, "公式变量、常量和函数配置不能为 null");
        if (request.Formula.Length > 8192 || request.Variables.Count > 64 || request.Constants.Count > 64 ||
            request.Functions.Count > 64 || request.Functions.Any(x => x.Length > 8192))
            return new(false, null, "公式配置超出大小限制；数学运算类型不受限制");
        var license = options.Value;
        if (scope != null)
        {
            using var operation = DatabaseOperationScope.Begin(scope);
            var db = DatabaseOperationScope.GetConnection(scope, "calmadmin");
            var setting = await db.Queryable<ProcessBoardSettingEntity>().InSingleAsync(1);
            if (setting == null) return new(false, null, "公式设置尚未初始化，请稍后重试");
            // 数据库是授权配置的唯一来源；环境配置只提供计算预算，不能绕过管理员停用。
            license = new ProcessFormulaOptions
            {
                UseType = setting.FormulaUseType ?? "Personal", Signature = setting.FormulaSignature ?? string.Empty,
                TermsAccepted = setting.FormulaTermsAccepted, TimeoutSeconds = options.Value.TimeoutSeconds
            };
        }
        if (!license.TermsAccepted || string.IsNullOrWhiteSpace(license.Signature))
            return new(false, null, "mXparser 个人使用配置尚未确认署名及非商业条款");
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        budget.CancelAfter(TimeSpan.FromSeconds(Math.Clamp(license.TimeoutSeconds, 1, 30)));
        var acquired = false;
        Process? process = null;
        try
        {
            await Workers.WaitAsync(budget.Token);
            acquired = true;
            var executable = Environment.ProcessPath ?? throw new InvalidOperationException("无法定位计算进程");
            var start = new ProcessStartInfo(executable)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            if (Path.GetFileNameWithoutExtension(executable).Equals("dotnet", StringComparison.OrdinalIgnoreCase))
                start.ArgumentList.Add(Assembly.GetEntryAssembly()!.Location);
            start.ArgumentList.Add("--process-formula-worker");
            process = Process.Start(start) ?? throw new InvalidOperationException("无法启动计算进程");
            var stdout = process.StandardOutput.ReadToEndAsync(budget.Token);
            var stderr = process.StandardError.ReadToEndAsync(budget.Token);
            await process.StandardInput.WriteLineAsync(JsonConvert.SerializeObject(new ProcessFormulaWorkerRequest
            {
                License = license, Expression = request
            }).AsMemory(), budget.Token);
            process.StandardInput.Close();
            await process.WaitForExitAsync(budget.Token);
            var output = await stdout;
            await stderr; // 持续排空，防止错误输出管道写满阻塞。
            var payload = output.Split('\n').LastOrDefault(x => x.StartsWith("PROCESS_FORMULA_RESULT:"));
            if (payload == null) return new(false, null, "隔离计算进程未返回结果");
            return JsonConvert.DeserializeObject<ProcessFormulaResult>(payload["PROCESS_FORMULA_RESULT:".Length..].Trim())
                ?? new(false, null, "计算结果无法解析");
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return new(false, null, "公式计算超时，请缩小计算规模或重试");
        }
        finally
        {
            if (process != null)
            {
                try
                {
                    if (!process.HasExited) process.Kill(entireProcessTree: true);
                }
                finally
                {
                    process.Dispose();
                }
            }
            if (acquired) Workers.Release();
        }
    }
}
