using Microsoft.Extensions.Configuration;
using SqlSugar;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Service
{
    public enum DbType { BatteryTracker, recipe_results_db_fr, recipe_results_db_hc,recipe_results_db_ocv, recipe_results_db_dcir }

    public class DbFactory
    {
        private readonly IConfiguration _config;
        private readonly ConcurrentDictionary<DbType, SqlSugarScope> _clients = new();

        public DbFactory(IConfiguration config) => _config = config;

        public SqlSugarScope GetDb(DbType type)
        {
            return _clients.GetOrAdd(type, key => {
                var connStr = _config.GetConnectionString($"{key}");
                return new SqlSugarScope(new ConnectionConfig
                {
                    ConnectionString = connStr,
                    DbType = SqlSugar.DbType.SqlServer,
                    IsAutoCloseConnection = true
                }, db => {
                    // AOP配置示例 
                    db.Aop.OnLogExecuting = (sql, pars) => {
                        Console.WriteLine($"【{key}库执行SQL】\n{sql}");
                    };
                });
            });
        }
    }
}
