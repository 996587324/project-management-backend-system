using Model.ProcessBoard;
using Newtonsoft.Json;
using org.mariuszgromada.math.mxparser;

namespace Service;

/// <summary>只在隔离计算进程执行；不创建 Host、不连接数据库、不启动采集服务。</summary>
public static class ProcessFormulaWorker
{
    public static async Task RunAsync()
    {
        var line = await Console.In.ReadLineAsync();
        ProcessFormulaResult result;
        try
        {
            var request = JsonConvert.DeserializeObject<ProcessFormulaWorkerRequest>(line ?? "")
                ?? throw new ArgumentException("公式请求为空");
            result = Evaluate(request);
        }
        catch (Exception exception)
        {
            result = new ProcessFormulaResult(false, null, exception.Message);
        }
        // 授权提示等库输出不会被误认为结果；父进程只读取这个前缀。
        Console.WriteLine("PROCESS_FORMULA_RESULT:" + JsonConvert.SerializeObject(result));
    }

    public static ProcessFormulaResult Evaluate(ProcessFormulaWorkerRequest request)
    {
        var license = request.License;
        if (!license.TermsAccepted || string.IsNullOrWhiteSpace(license.Signature))
            return new(false, null, "请配置真实使用者署名并确认 mXparser 授权条款；个人模式仅限非商业用途");
        switch (license.UseType)
        {
            case "Personal":
                License.iConfirmNonCommercialUse(license.Signature);
                break;
            case "Commercial":
                License.iConfirmCommercialUse(license.Signature);
                break;
            default:
                return new(false, null, "mXparser 使用类型必须为 Personal 或 Commercial");
        }
        return EvaluateExpression(request.Expression);
    }

    /// <summary>集成测试可单独调用，不执行不实授权声明。业务入口必须通过 Evaluate。</summary>
    public static ProcessFormulaResult EvaluateExpression(ProcessFormulaRequest request)
    {
        var expression = new Expression(request.Formula);
        var definitions = new List<PrimitiveElement>();
        foreach (var variable in request.Variables)
        {
            if (!double.IsFinite(variable.Value)) return new(false, null, "变量值不是有效有限数值");
            definitions.Add(new Argument(variable.Key, variable.Value));
        }
        foreach (var constant in request.Constants)
        {
            if (!double.IsFinite(constant.Value)) return new(false, null, "常量值不是有效有限数值");
            definitions.Add(new Constant(constant.Key, constant.Value));
        }
        var functions = request.Functions.Select(x => new Function(x)).ToArray();
        definitions.AddRange(functions);
        foreach (var function in functions)
            function.addDefinitions(definitions.Where(x => !ReferenceEquals(x, function)).ToArray());
        expression.addDefinitions(definitions.ToArray());
        if (!expression.checkSyntax()) return new(false, null, expression.getErrorMessage());
        var value = expression.calculate();
        return double.IsFinite(value)
            ? new(true, value, null)
            : new(false, null, "计算结果无效（例如除零、溢出或不满足函数定义域）");
    }
}
