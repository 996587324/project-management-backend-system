using System;
using System.Collections.Generic;
using System.Linq;
using System.Linq.Expressions;
using System.Text;
using System.Threading.Tasks;

namespace Service
{
    public class DataService
    {
        private readonly DbFactory _dbFactory;

        public DataService(DbFactory dbFactory) => _dbFactory = dbFactory;
        // 动态指定数据库查询 
        public List<T> Query<T>(DbType dbType, Expression<Func<T, bool>> where)
            where T : class, new()
        {
            return _dbFactory.GetDb(dbType).Queryable<T>().Where(where).ToList();
        }
        public List<T> Query<T>(DbType dbType)
       where T : class, new()
        {
            return _dbFactory.GetDb(dbType).Queryable<T>().ToList();
        }
        public void CrossDbOperation()
        {
            var BatteryTracker = _dbFactory.GetDb(DbType.BatteryTracker);
            var recipe_results_db_fr = _dbFactory.GetDb(DbType.recipe_results_db_fr);
            var recipe_results_db_hc = _dbFactory.GetDb(DbType.recipe_results_db_hc);
            var recipe_results_db_ocv = _dbFactory.GetDb(DbType.recipe_results_db_ocv);
            var recipe_results_db_dcir = _dbFactory.GetDb(DbType.recipe_results_db_dcir);
           
        }
    }
}
