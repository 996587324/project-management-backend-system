using Model;

namespace 上传MES.Config
{
    public class ResultHelper
    {
        public static ApiResult Success(object res)
        {
            return new ApiResult() { IsSuccess = true, Result = res };
        }
        public static ApiResult Error(string message)
        {
            return new ApiResult() { IsSuccess = false, Msg = message };
        }
        public static BatCodeResult Success1(List<BatCodeList> list)
        {
            return new BatCodeResult() { code = 0, BatList =  list };
        }
        public static BatCodeResult Error1(string message, List<BatCodeList> list)
        {
            return new BatCodeResult() { code = 1, Msg = message , BatList = list };
        }
    

     
    }
}
