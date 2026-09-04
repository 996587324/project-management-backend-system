using Interface;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;
using Model.Other;
using Model.Pda;
using Model.User;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using SqlSugar;
using System.Diagnostics;
using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;
using System.Text.RegularExpressions;

namespace Service;

public class PdaService : IPdaService
{
    private const string DbConfigId = "calmadmin";
    private const string BusinessTypeProductBarcode = "ProductBarcode";
    private const string HandlerCodeProductBarcode = "ProductBarcodeQuery";

    private readonly SqlSugarScope _db;
    private readonly ICustomJWTService _jwtService;
    private readonly JWTTokenOptions _jwtOptions;
    private readonly PdaOptions _pdaOptions;
    private readonly IHttpClientFactory _httpClientFactory;

    public PdaService(
        SqlSugarScope db,
        ICustomJWTService jwtService,
        IOptionsMonitor<JWTTokenOptions> jwtOptions,
        IOptionsMonitor<PdaOptions> pdaOptions,
        IHttpClientFactory httpClientFactory)
    {
        _db = db;
        _jwtService = jwtService;
        _jwtOptions = jwtOptions.CurrentValue;
        _pdaOptions = pdaOptions.CurrentValue;
        _httpClientFactory = httpClientFactory;
    }

    private ISqlSugarClient Db => _db.GetConnection(DbConfigId);

    public async Task<PdaLoginResult> LoginAsync(PdaLoginRequest request)
    {
        try
        {
            await EnsureTablesAsync();
            var userName = request.userName?.Trim() ?? string.Empty;
            var password = request.password ?? string.Empty;
            var deviceCode = NormalizeDeviceCode(request.deviceCode);

            if (string.IsNullOrWhiteSpace(userName) || string.IsNullOrWhiteSpace(password) || string.IsNullOrWhiteSpace(deviceCode))
                throw new InvalidOperationException("账号、密码和设备编号不能为空");

            var user = await Db.Queryable<SysUserInfo>()
                .FirstAsync(x => x.UserName == userName && x.Password == password && x.Enabled);
            if (user == null)
                throw new InvalidOperationException("账号不存在、密码错误或账号已停用");

            if (!await HasPermissionAsync(user, "PdaScan"))
            {
                await WriteLoginLogAsync(user.UserId, user.UserName, deviceCode, false, "当前账号没有 PDA 扫码权限");
                throw new InvalidOperationException("当前账号没有 PDA 扫码权限");
            }

            var device = await ValidateDeviceAsync(deviceCode, user.UserId);
            device.LastOnlineTime = DateTime.Now;
            device.UpdateTime = DateTime.Now;
            await Db.Updateable(device)
                .UpdateColumns(x => new { x.LastOnlineTime, x.UpdateTime })
                .ExecuteCommandAsync();

            await WriteLoginLogAsync(user.UserId, user.UserName, device.DeviceCode, true, "登录成功");

            return new PdaLoginResult
            {
                token = CreatePdaToken(user, device.DeviceCode),
                refreshToken = _jwtService.GenerateRefreshToken(),
                userId = user.UserId,
                userName = user.UserName,
                roles = user.RoleList,
                permissionKeys = await GetPermissionKeysAsync(user),
                device = ToDeviceDto(device)
            };
        }
        catch (Exception ex)
        {
            throw ex;
        }

    }

    public async Task<PdaScanResultDto> QueryProductBarcodeAsync(
        string userId,
        string userName,
        string deviceCode,
        PdaScanQueryRequest request,
        CancellationToken cancellationToken)
    {
        await EnsureTablesAsync();
        var traceId = Guid.NewGuid().ToString("N");
        var rawBarcode = request.barcode ?? string.Empty;
        var barcode = NormalizeBarcode(rawBarcode);
        var scanId = string.IsNullOrWhiteSpace(request.scanId) ? Guid.NewGuid().ToString() : request.scanId.Trim();
        var scanTime = DateTime.Now;
        var watch = Stopwatch.StartNew();

        if (string.IsNullOrWhiteSpace(userId)) throw new InvalidOperationException("无法识别当前登录用户");
        if (string.IsNullOrWhiteSpace(deviceCode)) throw new InvalidOperationException("无法识别当前 PDA 设备");

        var existing = await Db.Queryable<PdaScanLog>()
            .FirstAsync(x => x.DeviceCode == deviceCode && x.ScanId == scanId);
        if (existing != null)
        {
            return new PdaScanResultDto
            {
                success = existing.Success,
                resultCode = existing.ResultCode,
                message = existing.Message,
                businessType = existing.BusinessType ?? BusinessTypeProductBarcode,
                data = ParseResultData(existing.ResultData),
                traceId = existing.TraceId,
                scanTime = existing.ScanTime
            };
        }

        PdaScanResultDto result;
        PdaBarcodeRule? rule = null;
        try
        {
            if (string.IsNullOrWhiteSpace(barcode))
                throw new InvalidOperationException("产品条码不能为空");

            var user = await Db.Queryable<SysUserInfo>().FirstAsync(x => x.UserId == userId && x.Enabled);
            if (user == null) throw new InvalidOperationException("当前登录用户不存在或已停用");
            if (!await HasPermissionAsync(user, "PdaScan")) throw new InvalidOperationException("当前账号没有 PDA 扫码权限");

            await ValidateDeviceAsync(deviceCode, userId);
            rule = await ValidateBarcodeRuleAsync(barcode, scanTime, cancellationToken);
            var handlerResult = await QueryProductBarcodeRemoteAsync(barcode, scanTime, cancellationToken);

            result = new PdaScanResultDto
            {
                success = handlerResult.Success,
                resultCode = handlerResult.Code,
                message = handlerResult.Message,
                businessType = BusinessTypeProductBarcode,
                data = handlerResult.Data,
                traceId = traceId,
                scanTime = scanTime
            };
        }
        catch (Exception ex)
        {
            result = new PdaScanResultDto
            {
                success = false,
                resultCode = "PDA_QUERY_FAILED",
                message = ex.Message,
                businessType = BusinessTypeProductBarcode,
                data = new { productBarcode = barcode },
                traceId = traceId,
                scanTime = scanTime
            };
        }
        finally
        {
            watch.Stop();
        }

        await Db.Insertable(new PdaScanLog
        {
            ScanId = scanId,
            Barcode = barcode,
            RawBarcode = rawBarcode == barcode ? null : Limit(rawBarcode, 256),
            DeviceCode = deviceCode,
            UserId = userId,
            UserName = userName,
            BusinessType = BusinessTypeProductBarcode,
            RuleCode = rule == null ? null : $"{rule.RuleCode}:v{rule.Version}",
            Success = result.success,
            ResultCode = result.resultCode,
            Message = Limit(result.message, 512),
            ResultData = Limit(JsonConvert.SerializeObject(result.data), 4000),
            DurationMs = (int)Math.Min(watch.ElapsedMilliseconds, int.MaxValue),
            TraceId = result.traceId,
            ScanTime = scanTime
        }).ExecuteCommandAsync();

        return result;
    }

    public async Task<PdaPageResult<PdaScanLogDto>> GetScanLogsAsync(PdaScanLogQueryDto query)
    {
        await EnsureTablesAsync();
        RefAsync<int> total = 0;
        var current = Math.Max(query.current, 1);
        var size = Math.Clamp(query.size, 1, 100);
        var dbQuery = Db.Queryable<PdaScanLog>();

        if (!string.IsNullOrWhiteSpace(query.barcode)) dbQuery = dbQuery.Where(x => x.Barcode.Contains(query.barcode.Trim()));
        if (!string.IsNullOrWhiteSpace(query.deviceCode)) dbQuery = dbQuery.Where(x => x.DeviceCode.Contains(query.deviceCode.Trim()));
        if (!string.IsNullOrWhiteSpace(query.userId)) dbQuery = dbQuery.Where(x => x.UserId == query.userId.Trim());
        if (query.success.HasValue) dbQuery = dbQuery.Where(x => x.Success == query.success.Value);
        if (query.startTime.HasValue) dbQuery = dbQuery.Where(x => x.ScanTime >= query.startTime.Value);
        if (query.endTime.HasValue) dbQuery = dbQuery.Where(x => x.ScanTime <= query.endTime.Value);

        var records = await dbQuery.OrderByDescending(x => x.ScanTime).ToPageListAsync(current, size, total);
        return Page(records.Select(ToScanLogDto).ToList(), current, size, total);
    }

    public async Task<PdaScanLogDto?> GetScanLogAsync(long id)
    {
        await EnsureTablesAsync();
        var entity = await Db.Queryable<PdaScanLog>().FirstAsync(x => x.Id == id);
        return entity == null ? null : ToScanLogDto(entity);
    }

    public async Task<PdaPageResult<PdaScanLogDto>> GetRecentScanLogsAsync(string userId, string deviceCode, int current, int size)
    {
        await EnsureTablesAsync();
        RefAsync<int> total = 0;
        current = Math.Max(current, 1);
        size = Math.Clamp(size, 1, 50);
        var records = await Db.Queryable<PdaScanLog>()
            .Where(x => x.UserId == userId && x.DeviceCode == deviceCode)
            .OrderByDescending(x => x.ScanTime)
            .ToPageListAsync(current, size, total);
        return Page(records.Select(ToScanLogDto).ToList(), current, size, total);
    }

    public async Task<PdaPageResult<PdaDeviceDto>> GetDevicesAsync(PdaDeviceQueryDto query)
    {
        await EnsureTablesAsync();
        RefAsync<int> total = 0;
        var current = Math.Max(query.current, 1);
        var size = Math.Clamp(query.size, 1, 100);
        var dbQuery = Db.Queryable<PdaDevice>();
        if (!string.IsNullOrWhiteSpace(query.keyword))
        {
            var keyword = query.keyword.Trim();
            dbQuery = dbQuery.Where(x =>
                x.DeviceCode.Contains(keyword) ||
                x.DeviceName.Contains(keyword) ||
                (x.Model != null && x.Model.Contains(keyword)));
        }
        if (query.enabled.HasValue) dbQuery = dbQuery.Where(x => x.Enabled == query.enabled.Value);

        var records = await dbQuery.OrderByDescending(x => x.CreateTime).ToPageListAsync(current, size, total);
        return Page(records.Select(ToDeviceDto).ToList(), current, size, total);
    }

    public async Task<PdaDeviceDto> SaveDeviceAsync(PdaDeviceSaveDto dto)
    {
        try
        {
            await EnsureTablesAsync();
            var code = NormalizeDeviceCode(dto.deviceCode);
            if (string.IsNullOrWhiteSpace(code)) throw new InvalidOperationException("设备编号不能为空");
            if (string.IsNullOrWhiteSpace(dto.deviceName)) throw new InvalidOperationException("设备名称不能为空");

            var entityId = dto.id.GetValueOrDefault();
            var exists = entityId > 0
                ? await Db.Queryable<PdaDevice>().AnyAsync(x => x.DeviceCode == code && x.Id != entityId)
                : await Db.Queryable<PdaDevice>().AnyAsync(x => x.DeviceCode == code);
            if (exists) throw new InvalidOperationException("设备编号已存在");

            PdaDevice entity;
            if (entityId > 0)
            {
                entity = await Db.Queryable<PdaDevice>().FirstAsync(x => x.Id == entityId)
                    ?? throw new InvalidOperationException("设备不存在");
                entity.DeviceCode = code;
                entity.DeviceName = dto.deviceName.Trim();
                entity.Model = TrimToNull(dto.model);
                entity.ScannerMode = string.IsNullOrWhiteSpace(dto.scannerMode) ? "Keyboard" : dto.scannerMode.Trim();
                entity.Enabled = dto.enabled;
                entity.BoundUserId = TrimToNull(dto.boundUserId);
                entity.UpdateTime = DateTime.Now;
                await Db.Updateable(entity).ExecuteCommandAsync();
            }
            else
            {
                entity = new PdaDevice
                {
                    DeviceCode = code,
                    DeviceName = dto.deviceName.Trim(),
                    Model = TrimToNull(dto.model),
                    ScannerMode = string.IsNullOrWhiteSpace(dto.scannerMode) ? "Keyboard" : dto.scannerMode.Trim(),
                    Enabled = dto.enabled,
                    BoundUserId = TrimToNull(dto.boundUserId),
                    CreateTime = DateTime.Now,
                    UpdateTime = DateTime.Now
                };
                entity.Id = await Db.Insertable(entity).ExecuteReturnBigIdentityAsync();
                if (entity.Id <= 0)
                {
                    entity = await Db.Queryable<PdaDevice>().FirstAsync(x => x.DeviceCode == code)
                        ?? throw new InvalidOperationException("设备已保存，但未能取得有效 ID");
                }
            }

            EnsureValidId(entity.Id, "设备");

            return ToDeviceDto(entity);
        }
        catch (Exception)
        {
            throw;
        }

    }

    public async Task<bool> DeleteDeviceAsync(long id)
    {
        await EnsureTablesAsync();
        return await Db.Deleteable<PdaDevice>().Where(x => x.Id == id).ExecuteCommandAsync() > 0;
    }

    public async Task<PdaPageResult<PdaBarcodeRuleDto>> GetBarcodeRulesAsync(PdaBarcodeRuleQueryDto query)
    {
        await EnsureTablesAsync();
        RefAsync<int> total = 0;
        var current = Math.Max(query.current, 1);
        var size = Math.Clamp(query.size, 1, 100);
        var dbQuery = Db.Queryable<PdaBarcodeRule>();
        if (!string.IsNullOrWhiteSpace(query.keyword))
        {
            var keyword = query.keyword.Trim();
            dbQuery = dbQuery.Where(x => x.RuleCode.Contains(keyword) || x.RuleName.Contains(keyword) || x.BusinessType.Contains(keyword));
        }
        if (query.enabled.HasValue) dbQuery = dbQuery.Where(x => x.Enabled == query.enabled.Value);

        var records = await dbQuery.OrderBy(x => x.Priority).OrderByDescending(x => x.CreateTime).ToPageListAsync(current, size, total);
        return Page(records.Select(ToRuleDto).ToList(), current, size, total);
    }

    public async Task<PdaBarcodeRuleDto> SaveBarcodeRuleAsync(PdaBarcodeRuleSaveDto dto)
    {
        await EnsureTablesAsync();
        var code = dto.ruleCode?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(code)) throw new InvalidOperationException("规则编码不能为空");
        if (string.IsNullOrWhiteSpace(dto.ruleName)) throw new InvalidOperationException("规则名称不能为空");
        if (!string.Equals(dto.handlerCode, HandlerCodeProductBarcode, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("首期仅支持产品条码查询处理器");
        var validationSource = NormalizeValidationSource(dto.validationSource);

        var entityId = dto.id.GetValueOrDefault();
        var exists = entityId > 0
            ? await Db.Queryable<PdaBarcodeRule>().AnyAsync(x => x.RuleCode == code && x.Id != entityId)
            : await Db.Queryable<PdaBarcodeRule>().AnyAsync(x => x.RuleCode == code);
        if (exists) throw new InvalidOperationException("规则编码已存在");

        PdaBarcodeRule entity;
        if (entityId > 0)
        {
            entity = await Db.Queryable<PdaBarcodeRule>().FirstAsync(x => x.Id == entityId)
                ?? throw new InvalidOperationException("规则不存在");
            entity.RuleCode = code;
            entity.RuleName = dto.ruleName.Trim();
            entity.Prefix = TrimToNull(dto.prefix);
            entity.BarcodeLength = dto.barcodeLength;
            entity.Pattern = TrimToNull(dto.pattern);
            entity.BusinessType = BusinessTypeProductBarcode;
            entity.HandlerCode = HandlerCodeProductBarcode;
            entity.ValidationSource = validationSource;
            entity.Priority = dto.priority;
            entity.Enabled = dto.enabled;
            entity.Version += 1;
            entity.UpdateTime = DateTime.Now;
            await Db.Updateable(entity).ExecuteCommandAsync();
        }
        else
        {
            entity = new PdaBarcodeRule
            {
                RuleCode = code,
                RuleName = dto.ruleName.Trim(),
                Prefix = TrimToNull(dto.prefix),
                BarcodeLength = dto.barcodeLength,
                Pattern = TrimToNull(dto.pattern),
                BusinessType = BusinessTypeProductBarcode,
                HandlerCode = HandlerCodeProductBarcode,
                ValidationSource = validationSource,
                Priority = dto.priority,
                Enabled = dto.enabled,
                Version = 1,
                CreateTime = DateTime.Now,
                UpdateTime = DateTime.Now
            };
            entity.Id = await Db.Insertable(entity).ExecuteReturnBigIdentityAsync();
            if (entity.Id <= 0)
            {
                entity = await Db.Queryable<PdaBarcodeRule>().FirstAsync(x => x.RuleCode == code)
                    ?? throw new InvalidOperationException("规则已保存，但未能取得有效 ID");
            }
        }

        EnsureValidId(entity.Id, "规则");

        return ToRuleDto(entity);
    }

    public async Task<bool> DeleteBarcodeRuleAsync(long id)
    {
        await EnsureTablesAsync();
        return await Db.Deleteable<PdaBarcodeRule>().Where(x => x.Id == id).ExecuteCommandAsync() > 0;
    }

    private async Task EnsureTablesAsync()
    {
        Db.CodeFirst.SetStringDefaultLength(200)
            .InitTables(typeof(PdaDevice), typeof(PdaScanLog), typeof(PdaBarcodeRule), typeof(PdaLoginLog), typeof(SysUserInfo), typeof(SysRole));

        var hasDefaultRule = await Db.Queryable<PdaBarcodeRule>().AnyAsync(x => x.RuleCode == "PRODUCT_BARCODE_DEFAULT");
        if (!hasDefaultRule)
        {
            await Db.Insertable(new PdaBarcodeRule
            {
                RuleCode = "PRODUCT_BARCODE_DEFAULT",
                RuleName = "产品条码默认规则",
                BusinessType = BusinessTypeProductBarcode,
                HandlerCode = HandlerCodeProductBarcode,
                ValidationSource = "Local",
                Priority = 100,
                Enabled = true,
                Version = 1,
                CreateTime = DateTime.Now,
                UpdateTime = DateTime.Now
            }).ExecuteCommandAsync();
        }
    }

    private async Task<PdaDevice> ValidateDeviceAsync(string deviceCode, string userId)
    {
        var device = await Db.Queryable<PdaDevice>().FirstAsync(x => x.DeviceCode == deviceCode);
        if (device == null) throw new InvalidOperationException("PDA 设备未注册");
        if (!device.Enabled) throw new InvalidOperationException("PDA 设备已停用");
        if (!string.IsNullOrWhiteSpace(device.BoundUserId) &&
            !string.Equals(device.BoundUserId, userId, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("当前账号未绑定该 PDA 设备");

        return device;
    }

    private async Task<PdaBarcodeRule> ValidateBarcodeRuleAsync(
        string barcode,
        DateTime time,
        CancellationToken cancellationToken)
    {
        var rules = await Db.Queryable<PdaBarcodeRule>()
            .Where(x => x.Enabled && x.HandlerCode == HandlerCodeProductBarcode)
            .OrderBy(x => x.Priority)
            .ToListAsync();

        foreach (var rule in rules)
        {
            if (!string.IsNullOrWhiteSpace(rule.Prefix) && !barcode.StartsWith(rule.Prefix, StringComparison.OrdinalIgnoreCase)) continue;
            if (rule.BarcodeLength.HasValue && barcode.Length != rule.BarcodeLength.Value) continue;
            if (!string.IsNullOrWhiteSpace(rule.Pattern) && !Regex.IsMatch(barcode, rule.Pattern)) continue;

            if (NormalizeValidationSource(rule.ValidationSource) == "Remote")
            {
                var remoteResult = await QueryRemoteBarcodeRuleValidationAsync(barcode, time, cancellationToken);
                if (!remoteResult.Success)
                    throw new InvalidOperationException(string.IsNullOrWhiteSpace(remoteResult.Message)
                        ? "远程条码规则校验未通过"
                        : remoteResult.Message);
            }
            return rule;
        }

        throw new InvalidOperationException("产品条码不符合已启用规则");
    }

    /// <summary>调用后端配置的远程规则校验接口；PDA 客户端不会接触该地址或鉴权信息。</summary>
    private async Task<ProductBarcodeHandlerResult> QueryRemoteBarcodeRuleValidationAsync(
        string barcode,
        DateTime time,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(_pdaOptions.RemoteBarcodeRuleValidationUrl))
            throw new InvalidOperationException("远程条码规则校验接口未配置");

        var client = _httpClientFactory.CreateClient();
        client.Timeout = TimeSpan.FromSeconds(Math.Clamp(_pdaOptions.RemoteBarcodeRuleValidationTimeoutSeconds, 1, 60));
        using var request = new HttpRequestMessage(HttpMethod.Post, _pdaOptions.RemoteBarcodeRuleValidationUrl)
        {
            Content = new StringContent(JsonConvert.SerializeObject(new
            {
                productBarcode = barcode,
                time = time.ToString("o")
            }), Encoding.UTF8, "application/json")
        };
        if (!string.IsNullOrWhiteSpace(_pdaOptions.RemoteBarcodeRuleValidationApiKey))
            request.Headers.TryAddWithoutValidation("Authorization", _pdaOptions.RemoteBarcodeRuleValidationApiKey);

        using var response = await client.SendAsync(request, cancellationToken);
        var text = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            return new ProductBarcodeHandlerResult
            {
                Success = false,
                Code = $"HTTP_{(int)response.StatusCode}",
                Message = $"远程条码规则校验接口返回异常：{(int)response.StatusCode}",
                Data = new { productBarcode = barcode }
            };
        }
        return AdaptRemoteResponse(text, barcode);
    }

    private async Task<ProductBarcodeHandlerResult> QueryProductBarcodeRemoteAsync(string barcode, DateTime time, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(_pdaOptions.ProductBarcodeQueryUrl))
        {
            return new ProductBarcodeHandlerResult
            {
                Success = true,
                Code = "MOCK_PRODUCT_BARCODE",
                Message = "产品条码查询接口未配置，已返回占位结果",
                Data = new
                {
                    productBarcode = barcode,
                    queryTime = time.ToString("yyyy-MM-dd HH:mm:ss"),
                    status = "待接入业务接口"
                }
            };
        }

        var client = _httpClientFactory.CreateClient();
        client.Timeout = TimeSpan.FromSeconds(Math.Clamp(_pdaOptions.ProductBarcodeTimeoutSeconds, 1, 60));
        using var request = new HttpRequestMessage(HttpMethod.Post, _pdaOptions.ProductBarcodeQueryUrl)
        {
            Content = new StringContent(JsonConvert.SerializeObject(new
            {
                productBarcode = barcode,
                time = time.ToString("o")
            }), Encoding.UTF8, "application/json")
        };

        if (!string.IsNullOrWhiteSpace(_pdaOptions.ProductBarcodeApiKey))
            request.Headers.TryAddWithoutValidation("Authorization", _pdaOptions.ProductBarcodeApiKey);

        using var response = await client.SendAsync(request, cancellationToken);
        var text = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            return new ProductBarcodeHandlerResult
            {
                Success = false,
                Code = $"HTTP_{(int)response.StatusCode}",
                Message = $"产品条码接口返回异常：{(int)response.StatusCode}",
                Data = new { productBarcode = barcode }
            };
        }

        return AdaptRemoteResponse(text, barcode);
    }

    private static ProductBarcodeHandlerResult AdaptRemoteResponse(string text, string barcode)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return new ProductBarcodeHandlerResult
            {
                Success = false,
                Code = "EMPTY_RESPONSE",
                Message = "产品条码接口未返回数据",
                Data = new { productBarcode = barcode }
            };
        }

        try
        {
            var json = JObject.Parse(text);
            return new ProductBarcodeHandlerResult
            {
                Success = ReadBool(json, "success", "Success", "ok", "Ok") ?? true,
                Code = ReadString(json, "code", "Code", "resultCode", "ResultCode") ?? "OK",
                Message = ReadString(json, "message", "Message", "msg", "Msg") ?? "查询成功",
                Data = json["data"] ?? json["Data"] ?? json
            };
        }
        catch
        {
            return new ProductBarcodeHandlerResult
            {
                Success = true,
                Code = "TEXT_RESPONSE",
                Message = "产品条码接口已返回文本结果",
                Data = new { productBarcode = barcode, raw = Limit(text, 1000) }
            };
        }
    }

    private async Task<bool> HasPermissionAsync(SysUserInfo user, string permission)
    {
        var permissions = await GetPermissionKeysAsync(user);
        return permissions.Contains("*") || permissions.Contains(permission) || permissions.Contains("Pda");
    }

    private async Task<List<string>> GetPermissionKeysAsync(SysUserInfo user)
    {
        if (user.RoleList.Contains("R_ADMIN") || user.PermissionKeyList.Contains("*")) return new List<string> { "*" };
        var rolePermissions = new List<string>();
        if (user.RoleList.Any())
        {
            var roles = await Db.Queryable<SysRole>()
                .Where(x => user.RoleList.Contains(x.roleCode) && x.enabled)
                .ToListAsync();
            rolePermissions = roles.SelectMany(x => x.permissionKeyList).ToList();
        }

        return rolePermissions.Concat(user.PermissionKeyList)
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private string CreatePdaToken(SysUserInfo user, string deviceCode)
    {
        var claims = new[]
        {
            new Claim("Id", user.UserId),
            new Claim("Name", user.UserName),
            new Claim("UserType", "0"),
            new Claim("DeviceCode", deviceCode),
            new Claim("LoginSource", "PDA")
        };

        var key = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(_jwtOptions.SecurityKey));
        var creds = new SigningCredentials(key, SecurityAlgorithms.HmacSha256);
        var token = new JwtSecurityToken(
            issuer: _jwtOptions.Issuer,
            audience: _jwtOptions.Audience,
            claims: claims,
            expires: null,
            signingCredentials: creds);
        return new JwtSecurityTokenHandler().WriteToken(token);
    }

    private async Task WriteLoginLogAsync(string userId, string userName, string deviceCode, bool success, string message)
    {
        await Db.Insertable(new PdaLoginLog
        {
            UserId = userId,
            UserName = userName,
            DeviceCode = deviceCode,
            Success = success,
            Message = message,
            LoginTime = DateTime.Now
        }).ExecuteCommandAsync();
    }

    private static PdaDeviceDto ToDeviceDto(PdaDevice x) => new()
    {
        id = x.Id,
        deviceCode = x.DeviceCode,
        deviceName = x.DeviceName,
        model = x.Model,
        scannerMode = x.ScannerMode,
        enabled = x.Enabled,
        boundUserId = x.BoundUserId,
        lastOnlineTime = x.LastOnlineTime,
        createTime = x.CreateTime,
        updateTime = x.UpdateTime
    };

    private static PdaScanLogDto ToScanLogDto(PdaScanLog x) => new()
    {
        id = x.Id,
        scanId = x.ScanId,
        barcode = x.Barcode,
        deviceCode = x.DeviceCode,
        userId = x.UserId,
        userName = x.UserName,
        businessType = x.BusinessType,
        success = x.Success,
        resultCode = x.ResultCode,
        message = x.Message,
        resultData = x.ResultData,
        durationMs = x.DurationMs,
        traceId = x.TraceId,
        scanTime = x.ScanTime
    };

    private static PdaBarcodeRuleDto ToRuleDto(PdaBarcodeRule x) => new()
    {
        id = x.Id,
        ruleCode = x.RuleCode,
        ruleName = x.RuleName,
        prefix = x.Prefix,
        barcodeLength = x.BarcodeLength,
        pattern = x.Pattern,
        businessType = x.BusinessType,
        handlerCode = x.HandlerCode,
        validationSource = NormalizeValidationSource(x.ValidationSource),
        priority = x.Priority,
        enabled = x.Enabled,
        version = x.Version,
        createTime = x.CreateTime,
        updateTime = x.UpdateTime
    };

    private static string NormalizeValidationSource(string? value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Equals("Local", StringComparison.OrdinalIgnoreCase)) return "Local";
        if (value.Equals("Remote", StringComparison.OrdinalIgnoreCase)) return "Remote";
        throw new InvalidOperationException("规则校验来源仅支持 Local 或 Remote");
    }

    private static PdaPageResult<T> Page<T>(List<T> records, int current, int size, int total) => new()
    {
        records = records,
        current = current,
        size = size,
        total = total
    };

    private static string NormalizeBarcode(string value) => (value ?? string.Empty).Trim();
    private static string NormalizeDeviceCode(string value) => (value ?? string.Empty).Trim().ToUpperInvariant();
    private static string? TrimToNull(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    private static string Limit(string? value, int max) => string.IsNullOrEmpty(value) ? string.Empty : value.Length <= max ? value : value[..max];

    private static void EnsureValidId(long id, string resourceName)
    {
        if (id <= 0) throw new InvalidOperationException($"{resourceName} ID 缺失或无效");
    }

    private static object? ParseResultData(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        try { return JsonConvert.DeserializeObject(value); }
        catch { return value; }
    }

    private static string? ReadString(JObject json, params string[] names)
    {
        foreach (var name in names)
        {
            var token = json[name];
            if (token != null) return token.Type == JTokenType.String ? token.Value<string>() : token.ToString(Formatting.None);
        }
        return null;
    }

    private static bool? ReadBool(JObject json, params string[] names)
    {
        foreach (var name in names)
        {
            var token = json[name];
            if (token == null) continue;
            if (token.Type == JTokenType.Boolean) return token.Value<bool>();
            if (bool.TryParse(token.ToString(), out var value)) return value;
        }
        return null;
    }

    private sealed class ProductBarcodeHandlerResult
    {
        public bool Success { get; init; }
        public string Code { get; init; } = string.Empty;
        public string Message { get; init; } = string.Empty;
        public object? Data { get; init; }
    }
}
